# Architecture: narrow identity-fact projection (`materialization_facts`)

> **Status:** PROPOSED — design pass only, no code moves until Boss approves.
> **Directive (Boss, 2026-07-16):** "for things that are only used for materialization, we
> should be using something more like the fact_history table (maybe a separate table) with
> actual facts, than a table we have to keep adding columns to." Ratified direction from the
> follow-up discussion: a fact-shaped **current-value** table maintained at ingest — *not*
> direct queries against `facts_history` (history dedup on every pass, retention coupling).
>
> Companion background: `docs/plans/architecture-operator-facts.md` (NFR-8 exclusion set and
> fitness tests, which this design reshapes), AGENTS.md "Discovery materialization".
>
> Every claim below about columns, consumers, and mechanics was verified against the primary
> source this session (file references inline). Query rewrites marked **worked** are complete;
> ones marked **sketch** follow the same pattern but their final SQL is a Phase-2 task done
> under the equality-fitness net (§7).

---

## 1. Problem

Adding one materializer-only signal today costs five touch points:

1. a migration adding a column to `proj_discovered`,
2. a `ProjectionLibrary.cs` column def,
3. an `IdentityInputColumns` entry in `DiscoveryMaterializer.cs`,
4. an `OperatorFactCatalog.IdentityBearingFactPaths` entry,
5. the NFR-8 fitness-test mapping.

`proj_discovered` shows the churn: `obscured_mac` (0021), `cast_id` (0025), `ssh_host_key`
(0026), `hue_bridge_id` + `onvif_hardware_id` (0043) were each a column-per-signal migration,
and every future scanner fingerprint repeats the pattern. These columns exist **only** so
`DiscoveryMaterializer` can read current values back out — no report sorts, filters, or joins
on them. A wide table is the wrong shape for an open-ended set of single-consumer signals;
a narrow fact-shaped table makes a new signal a **data change** (add a path to a set), not a
schema change.

## 2. Evidence: who reads which `proj_discovered` column

18 columns today (`ProjectionLibrary.cs` `proj_discovered` def). Consumers verified by reading
every query that touches the table:

| Column | Materializer | Reports / other | Verdict |
|---|---|---|---|
| `mac` | fingerprint + promote (many passes) | `GetDeviceSightings`, `GetDeviceSummary`, `GetDeviceAdvertisedServices`, `DeviceListApi` (joins), OUI display | **stays wide** |
| `obscured_mac` | obscured-MAC pass | reports use `obscured_mac IS NULL` as a row-trust guard (`GetDeviceSightings.sql:33`, `GetDeviceSummary.sql:23`, `DeviceListApi.cs:327`, `GetPromotionGapRows.sql`) | **stays wide** |
| `hostname` | promote, gap fill | `ListTargetCandidates`, `GetDeviceSummary`, `DeviceListApi` | **stays wide** |
| `friendly_name` | promote, gap fill | display rollup (`GetDeviceSummary.sql:19`, `DeviceListApi.cs:324`) | **stays wide** |
| `sources` | — | `GetDeviceSightings`, `DeviceListApi` source filter, `ListTargetCandidates` | **stays wide** |
| `vendor` | promote, gap fill | `ListTargetCandidates` (google-wifi candidacy) | **stays wide** |
| `model` | promote, gap fill | `ListTargetCandidates` | **stays wide** |
| `ssh_host_key` | `GetSshHostKeyRows` (fingerprint) | `ListTargetCandidates.sql:33` — presence test only | **moves** (rewrite one predicate to an `EXISTS`, §6) |
| `onvif_serial` | fingerprint + promote | — | **moves** |
| `roku_serial` | fingerprint + promote | — | **moves** |
| `snmp_serial` | fingerprint + promote | — | **moves** |
| `ssdp_uuid` | fingerprint + promote | — | **moves** |
| `wsd_uuid` | fingerprint + promote | — | **moves** |
| `hue_bridge_id` | `GetScannerIdRows` | — | **moves** |
| `onvif_hardware_id` | `GetScannerIdRows` | — | **moves** |
| `cast_id` | obscured-MAC pass, `GetCastIdIpCounts` | — | **moves** |
| `device_type` | `GetObscuredMacRows` → kind promotion | — | **moves** |
| `os` | discovered promote, gap fill | — | **moves** |

**Moving set: 11 paths**, all `Device[].Discovered[].*`, all text-valued:
`OnvifSerial, RokuSerial, SnmpSerial, SsdpUuid, WsdUuid, HueBridgeId, OnvifHardwareId,
CastId, DeviceType, Os, SshHostKey`.

The wide `proj_discovered` keeps its report-facing identity: (device, discovered-ip, mac,
obscured_mac, hostname, friendly_name, sources, vendor, model). `mac`/`obscured_mac` stay wide
deliberately even though they are fingerprints — reports join on `mac` and trust-guard on
`obscured_mac IS NULL`, and `SetDiscoveredMac.sql` writes the reconstructed MAC back into the
row. Identity-signal ≠ materializer-only; the split is by **consumer**, not by semantics.

## 3. Table shape

```sql
-- One row = the CURRENT value of one identity-signal fact. Fact-shaped (path, not column),
-- so new signals never need DDL. Text-only by design: every identity signal is a string
-- identifier; a non-string signal has no business being a fingerprint.
CREATE TABLE materialization_facts (
    device         TEXT        NOT NULL,  -- device GUID (first dimension key)
    entity_key     TEXT        NOT NULL DEFAULT '',  -- non-device dimension keys, path order,
                                                     -- '\0'-joined; '' for device-scoped paths.
                                                     -- For Discovered[]: the neighbor IP.
    attribute_path TEXT        NOT NULL,  -- full FactPaths template, e.g. 'Device[].Discovered[].SsdpUuid'
    value          TEXT        NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, entity_key, attribute_path)
);

-- Fingerprint lookups ("which rows carry ssdp_uuid = X") and the new-since-pass anti-joins.
CREATE INDEX materialization_facts_path_value_idx ON materialization_facts (attribute_path, value);
-- Retention sweep (same updated_at-staleness model as proj_discovered).
CREATE INDEX materialization_facts_updated_idx ON materialization_facts (updated_at);
```

- **Text-only values** is a hard rule, enforced at routing time (drop + warn on non-string).
  It keeps the table one-typed and the pivots cast-free. This is not a general EAV store —
  it is the identity-signal projection, nothing else routes here.
- **Retention:** register in `retention_policies` with the same `INTERVAL '7 days'` /
  rationale as `proj_discovered` (0008) — stale discovered-neighbor signals age out together.
  A row's `updated_at` advances on every re-observation (upsert), same as the wide table.

## 4. Routing (write path)

New `IdentityFactProjection : IProjection`, registered with the existing `ProjectionRouter`
for each path in a new declarative set:

```csharp
/// <summary>The identity-signal paths projected into materialization_facts. Adding a new
/// scanner fingerprint = add its FactPaths const here. No migration.</summary>
public static class IdentitySignalPaths
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FactPaths.DiscoveredOnvifSerial, FactPaths.DiscoveredRokuSerial,
        FactPaths.DiscoveredSnmpSerial,  FactPaths.DiscoveredSsdpUuid,
        FactPaths.DiscoveredWsdUuid,     FactPaths.DiscoveredHueBridgeId,
        FactPaths.DiscoveredOnvifHardwareId, FactPaths.DiscoveredCastId,
        FactPaths.DiscoveredDeviceType,  FactPaths.DiscoveredOs,
        FactPaths.DiscoveredSshHostKey,
    };
}
```

- The router's exact `(DimKey, Attribute)` index already supports multiple projections per
  key (`ProjectionRouter.cs` `_index` is `List<IProjection>`), so **no router changes**:
  during the dual-write phase the same routed fact hits both `GenericProjection`
  (`proj_discovered`) and `IdentityFactProjection`.
- Upsert semantics mirror `GenericProjection` (`GenericProjection.cs:131-200`): batched
  array-parameter upsert, `ON CONFLICT (device, entity_key, attribute_path) DO UPDATE SET
  value = EXCLUDED.value, updated_at = EXCLUDED.updated_at WHERE materialization_facts.value
  IS DISTINCT FROM EXCLUDED.value OR ...updated_at < EXCLUDED.updated_at`, fronted by the
  same entity-state cache pattern (key = (device, entity_key, path)) so unchanged
  re-observations never touch Postgres.
- One semantic **difference** from the wide table, and it's a feature: `GenericProjection`
  preserves a column when a later batch omits it ("EXCLUDED IS NOT NULL" guard). The narrow
  table has per-fact rows, so an omitted signal is simply not touched — identical outcome,
  no per-column guard needed.
- `DiscoveryMaterializer.RelevantTables` gains `"materialization_facts"` so the touched-table
  gate keeps triggering the right passes.

## 5. Read path — pivot pattern and worked rewrites

The materializer reads become "pivot the entity's signal rows, join the wide row for the
report-shaped columns." Canonical pivot:

```sql
SELECT
    device
  , entity_key AS ip
  , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].OnvifSerial') AS onvif_serial
  , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].SsdpUuid')    AS ssdp_uuid
  -- … one line per signal the pass needs …
FROM materialization_facts
WHERE attribute_path = ANY($paths)
GROUP BY device, entity_key
```

**Worked — `GetCastIdIpCounts.sql`** (distinct-IP count per cast id; gets *simpler*):

```sql
SELECT value AS cast_id, count(DISTINCT entity_key) AS ip_count
FROM materialization_facts
WHERE attribute_path = 'Device[].Discovered[].CastId'
GROUP BY value
```

**Worked — `GetObscuredMacRows.sql`** (obscured pass; wide row keeps mac/obscured_mac/
hostname/model/friendly_name/vendor, narrow supplies cast_id/device_type/os):

```sql
SELECT
    d.device, d.discovered AS ip, d.obscured_mac, d.mac,
    d.hostname, d.model, d.friendly_name, d.vendor,
    idf.cast_id, idf.device_type, idf.os
FROM proj_discovered d
LEFT JOIN (
    SELECT device, entity_key,
           MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].CastId')     AS cast_id,
           MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].DeviceType') AS device_type,
           MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].Os')         AS os
    FROM materialization_facts
    WHERE attribute_path IN (…the three paths…)
    GROUP BY device, entity_key
) idf ON idf.device = d.device AND idf.entity_key = d.discovered
WHERE d.obscured_mac IS NOT NULL
```

**Worked — `ListTargetCandidates.sql` ssh predicate** (the one report read of a moving
column, presence-only):

```sql
-- was: OR d.ssh_host_key IS NOT NULL
OR EXISTS (SELECT 1 FROM materialization_facts f
           WHERE f.device = d.device AND f.entity_key = d.discovered
             AND f.attribute_path = 'Device[].Discovered[].SshHostKey')
```

**Sketch — `GetNewDiscoveredMacs.sql` / `GetNewDiscoveredSerials.sql` /
`GetScannerIdRows.sql` / `GetSshHostKeyRows.sql`:** same join+pivot; the
`device_fingerprints` anti-joins move onto the pivot columns (or, for the serial/uuid
families, directly onto narrow rows: `LEFT JOIN device_fingerprints f ON f.fp_type = '…'
AND f.fp_value = idf.value` with `idf.attribute_path = '…'` — the
`(attribute_path, value)` index serves this). "New since last pass" needs **no watermark
changes**: newness is fingerprint-absence, not time, so the mechanism is shape-independent.

**Sketch — `GetPromotionGapRows.sql`:** the `sightings` lateral pivots `os` (and only `os`)
from the narrow table alongside the wide columns; the per-column
"freshest non-null" semantics carry over per-row naturally (each narrow row has its own
`updated_at`, which is *more* precise than the wide row's shared one).

## 6. Cross-cutting obligations

These are where a drive-by would cause real damage; each gets an explicit artifact:

1. **Device merge repoint.** `DeviceRegistry.RepointProjectionsAsync` iterates
   `ProjectionLibrary.AllDefs` — the narrow table is not a `ProjectionDef`, so it must be
   repointed explicitly (`UPDATE … SET device = survivor WHERE device = loser`, with
   `ON CONFLICT` loser-row drop matching the existing collision behavior for other
   projections). The `proj_device_certs` lesson (silently skipped by a hardcoded list) says:
   add a **fitness test** asserting every table with a `device` column is covered by the
   merge repoint (catalog-driven: `ProjectionLibrary.AllDefs` ∪ an explicit extras list that
   the test pins to the information_schema reality).
2. **NFR-8 fitness restructure (simplification).** Today's value-arm maps
   `(table, column) → FactPaths const` through `ProjectionLibrary`. After the move, the
   narrow reads are path-direct, so the expected exclusion set becomes:
   `mapped(remaining wide IdentityInputColumns) ∪ IdentitySignalPaths ∪ {HwSystemSerial}
   − GapFillOnlyFactPaths`. `IdentityInputColumns` shrinks to the wide reads only;
   the eleven moved entries disappear from the hand-maintained list because
   `IdentitySignalPaths` *is* the declaration. Net: adding a signal touches the path set
   and nothing else — the fitness test keeps enforcing that the operator-facts write gate
   blocks it automatically.
3. **Operator-facts write gate.** `IdentitySignalPaths ⊆ IdentityBearingFactPaths` must hold
   (enforced by the restructured fitness test) so operators still cannot author identity
   signals. No behavior change intended.
4. **Retention parity.** `retention_policies` row for `materialization_facts`
   (7 days, same rationale as `proj_discovered`). Without it, signals for departed neighbors
   would keep resolving devices forever.
5. **Fact views / device detail.** Unaffected — fact views read `facts_history` directly,
   not projections. HA inline promotion is also unaffected (resolves off in-memory batch
   facts, per `DiscoveryMaterializer.cs:30-32`).

## 7. Staged migration plan

Each phase is independently deployable with green tests; the wide columns are the safety net
until the last step.

**Phase 1 — table + dual write + equality net.**
- Migration `00XX`: create `materialization_facts` + indexes + `retention_policies` row, and
  **backfill** from the eleven `proj_discovered` columns
  (`INSERT … SELECT device, discovered, '<path>', <col>, updated_at … WHERE <col> IS NOT NULL`,
  one arm per column).
- `IdentitySignalPaths` + `IdentityFactProjection`, registered alongside the existing defs
  (dual write). `RelevantTables` += the new table. Merge repoint + its fitness test (§6.1).
- **Equality fitness (integration):** after seeded ingest covering every signal, assert the
  narrow pivots equal the wide columns row-for-row. This test is the net under Phases 1–2
  and is deleted in Phase 3.

**Phase 2 — move the reads, one query family at a time.**
Order chosen so each PR is one materializer pass: (a) `GetCastIdIpCounts`;
(b) `GetScannerIdRows` + `GetSshHostKeyRows` + the `ListTargetCandidates` ssh predicate;
(c) `GetNewDiscoveredSerials`; (d) `GetNewDiscoveredMacs`; (e) `GetObscuredMacRows`
(+ `ObscuredRow` mapping); (f) `GetPromotionGapRows` (`os` arm only). Integration tests
(`ServerQueryValidationTests` + the materializer suites) run per step; the equality fitness
keeps proving reads against either shape see the same world.

**Phase 3 — retire the wide columns.**
- Migration dropping the eleven columns; remove their `ProjectionLibrary` defs (ends dual
  write); shrink `IdentityInputColumns` to the surviving wide reads; restructure the NFR-8
  fitness per §6.2; delete the equality fitness; update AGENTS.md's materializer section.

**Phase 4 — projection-table audit and retirement (Boss directive, 2026-07-16: "then we can
get rid of projection tables that are no longer being used").**
- Audit every `proj_*` table's remaining readers (grep `Data/`, `Api/`, `Pages/` excluding
  the materializer): any table whose last reader was a moved materializer read gets its
  `ProjectionLibrary` def removed and the table dropped by migration.
- **Honest current finding:** none of today's tables fully empties from this move alone —
  `proj_device_arp` (ArpApi, SubnetDetail, ResolveIpDevice), `proj_dhcp_leases` /
  `proj_dhcp_local_leases` (ListDhcpLeases, terrain, ListSubnetHostIps), and the slimmed
  `proj_discovered` all keep genuine report readers. The near-term payoff is the eleven
  dropped columns, not dropped tables. The durable payoff is the **policy**: a future
  materializer-only signal never mints a projection column or table again, and this audit
  re-runs whenever a report retires so that any projection losing its last reader is dropped
  rather than lingering (the `proj_bacnet_device`/`proj_modbus_*` drops during the fact-view
  unification pass are the precedent).
- **Audit result (2026-07-16, after Phases 1-3 landed):** ran as predicted — **no table dropped**.
  Every `proj_*` table the materializer reads (`proj_discovered`, `proj_interfaces`,
  `proj_hardware`, `proj_systems`, `proj_device_arp`, `proj_dhcp_leases`,
  `proj_dhcp_local_leases`) still has non-materializer report readers, and the slimmed
  `proj_discovered` keeps mac/obscured_mac/hostname/friendly_name/vendor/model/sources (read by
  DeviceListApi, the device-detail queries, ListSubnetHostIps, ListTargetCandidates).
  `materialization_facts`'s only non-materializer readers are the device-detail All-Facts sighting
  match and the target-candidate ssh predicate — both intended. The policy is now documented in
  AGENTS.md ("Materializer-only identity signals route to `materialization_facts`").

## 8. Trade-offs accepted (and rejected alternatives)

- **Pivot SQL is less readable than column selects.** Real cost, accepted in exchange for
  zero-DDL signal addition and the deletion of the per-signal plumbing (migration + def +
  three list entries). Mitigation: the canonical pivot CTE pattern in §5, kept consistent
  across queries.
- **Rejected: flag/index column on `facts_history`** (Boss's literal first suggestion).
  Workable (precedent: the `source = 2` partial index), but every pass would pay
  latest-row-per-id dedup over unbounded history, and the hot identity path would couple to
  history retention. The upserted current table gives the same flexibility without either.
- **Rejected: move everything / all projections to EAV.** Reports genuinely need wide rows
  (sort/filter/trgm/keyset). The split is consumer-driven; wide projections remain the right
  tool where reports read.
- **Performance:** at this fleet's scale the pivots are noise (the table is thousands of
  rows, PK + two indexes). The win is velocity and flexibility, not speed — stated plainly
  so nobody later mistakes this for an optimization.
- **New failure mode to watch:** a signal path added to a collector but *not* to
  `IdentitySignalPaths` silently projects nowhere. The existing `FactPathRoutingFitnessTests`
  (every const needs a projection column or fact view) must learn that "∈ IdentitySignalPaths"
  is a third valid routing — that update is part of Phase 1.

## 9. Acceptance criteria

1. Adding a hypothetical new fingerprint signal requires exactly: the `FactPaths` const, one
   `IdentitySignalPaths` entry, and the materializer read — no migration, no
   `ProjectionLibrary`/`IdentityInputColumns`/`OperatorFactCatalog` edits, and the NFR-8 +
   routing fitness tests pass without modification.
2. All existing unit + integration suites green after every phase.
3. Device merge repoints `materialization_facts` (covered by the new repoint fitness test).
4. Retention prunes stale identity-fact rows on the same policy as `proj_discovered`.
5. No behavior change observable in reports or the operator-facts write gate.
6. After Phase 4, no `proj_*` table exists without at least one non-materializer reader,
   and the audit method is documented so it re-runs when reports change.

## 10. Promotion completeness — all discovered facts, not a curated subset

> **Directive (Boss, 2026-07-16):** "if we're going to surface a device, all its data needs
> to be surfaced. but a device shouldn't be surfaced if it can't be fingerprinted. we still
> believe an obscured mac is unique, so it existed. the problem was, not all the facts about
> it were also promoted. when something is promoted, all its facts need to be promoted from
> the device that sees it." Folds the standalone "device shows a name not in its facts" bug
> (device `bde0832f`, "Lappy.local") into this plan.

### 10.1 The gap (verified this session)

A device promoted purely from discovery legitimately exists — its obscured-MAC is a unique
fingerprint (`device_fingerprints`), so surfacing it is correct. What's wrong is *incomplete*
promotion:

- Promotion writes **projection rows via direct SQL** — `DiscoveryMaterializer`'s
  `ResolveAndPromoteAsync` / `MaterializeDiscoveredSourceAsync` / `MaterializePromotionGapsAsync`
  call `UpsertDeviceSystem.sql` / `UpsertDeviceSummary.sql` / `UpsertDeviceHardware.sql`. **None
  of them writes `facts_history`.** So a discovered device's **All Facts tab is empty** even when
  its summary/tabs render a name, and the displayed name has no provenance trail (it lives under
  the *observer's* `Device[observer].Discovered[ip]` subtree, not under the promoted device).
- Only a **hand-picked subset** of columns moves (`hostname`, `friendly_name`, `model`,
  `device_type`→`kind`, `vendor`, `os`). Any other intrinsic the observer knows is dropped.
  (For `bde0832f` the observer — a Google Wifi AP — happened to know only the obscured MAC +
  `hostname`, so the visible symptom was just the empty All Facts tab; a richer observer would
  drop more.)

### 10.2 What "all its facts" means

The observer's `Device[observer].Discovered[ip].*` subtree already separates **intrinsic device
attributes** from **the sighting/Link edge** (AGENTS.md, "Google Wifi collector"). Promotion must
carry over the **intrinsic** set only:

- **Promote:** `Hostname`, `FriendlyName`, `Model`, `DeviceType`, `Vendor`, `Os`, the advertised
  `Service[]` names, and every identity signal this plan moves to `materialization_facts`
  (`OnvifSerial`, `SnmpSerial`, `SsdpUuid`, `CastId`, … — §3's moving set).
- **Never promote:** `Discovered[].Link.*` (SignalDbm, Tx/RxRate, Rx/TxBytes, Band, Medium,
  Guest, ConnectedSeconds). These are properties of *the observation*, not the device, and stay
  on the observer's row (rendered on the device's "Seen By" tab).

### 10.3 Why this belongs in this plan

Once intrinsic identity signals are **fact-shaped** in `materialization_facts` (keyed
`(device, entity_key, attribute_path)`), "promote all facts" stops being a hand-maintained
column list and becomes a **uniform re-emit**: for the promoted device, iterate the observer's
`Discovered[ip]` intrinsic rows and re-emit each as a `Device[promotedDeviceId].<attr>` fact.
This deletes the curated-subset failure mode by construction — the same reason the plan replaces
per-signal columns with a path set. The wide `proj_discovered` intrinsics that stay (hostname,
friendly_name, vendor, model — §2) are re-emitted alongside from their known paths.

### 10.4 Approach

Promotion **emits the intrinsic facts through the ingest path** (into `facts_history`, then the
normal projection router) under the promoted device, rather than writing `proj_*` directly:

- **Provenance:** each emitted fact carries `source_name` = the observing collector (e.g.
  `google-wifi`). The promoted name then *is* a real, attributed fact in the All Facts tab —
  directly resolving the "name not in its facts" report — instead of an untraceable projection
  write.
- **Idempotency:** re-emitting every materializer cycle must be a no-op when unchanged. Server
  emission has no agent delta-tracker, so gate on change (facts_history dedup-on-write +
  `GenericProjection`'s EntityStateCache already absorb identical re-emits; confirm the promoted
  path hits both).
- **No re-derivation loop:** emitted facts are `Device[deviceId].*` (not
  `Device[observer].Discovered[]`), so they are never themselves re-observed and re-promoted.
- **Fingerprint gate unchanged:** still mint/surface a device only from a valid fingerprint
  (obscured-MAC counts; a lone locally-administered/randomized real MAC does not).
- The COALESCE-precedence rule is unchanged: an authoritative agent-reported fact for the same
  device+path always wins over a promoted discovered value (promotion fills gaps, never clobbers).

### 10.5 Obligations and acceptance criteria (additive to §6, §9)

7. A discovered-only device's `facts_history` contains **every** intrinsic fact the observer
   reported for it (minus `Link.*`), with `source_name` = the observing collector — asserted by
   an integration test that seeds an observer `Discovered[ip]` subtree, promotes, and diffs the
   promoted device's fact set against the observer's intrinsic set.
8. The promoted device's All Facts tab is non-empty whenever the observer knew any intrinsic
   about it; the displayed name resolves to a real fact with a collector source.
9. `Link.*` sighting telemetry never appears under the promoted device (only on the observer's
   "Seen By" row).

### 10.6 Sequencing

Runs as **Phase 5**, after the intrinsic identity signals are fact-shaped (Phases 1–3) so the
re-emit iterates `materialization_facts` + the surviving wide intrinsics uniformly. It is not a
prerequisite for Phases 1–4 and does not change their acceptance criteria.

### 10.7 Open question — promoted-fact precedence vs the last-write-wins projection (UNSETTLED)

> **Status (2026-07-16):** Flagged during review, **not yet resolved**. §10.4 asserts "promotion
> fills gaps, never clobbers" as if it were free. It is not — that guarantee is currently a
> property of the *direct-upsert* path only, and does **not** survive re-emitting through the
> normal ingest pipeline without extra work. Resolve this before implementing Phase 5.

The two write paths have **opposite precedence**, and the emit-facts approach silently swaps one
for the other:

| Path | `ON CONFLICT` rule | Effect |
|---|---|---|
| Promotion today (`Data/Discovery/UpsertDevice{Summary,Hardware,System}.sql`) | `COALESCE(existing, EXCLUDED)` | **fill-only / first-write-wins** — an already-set value is never overwritten |
| Normal pipeline (`Ingest/Projections/GenericProjection.cs` `BuildSql`) | `COALESCE(EXCLUDED, table)` | **last-write-wins** — newest non-null routed value wins |

`RoutedFact` (`Ingest/Projections/ProjectionRouter.cs`) carries only `DimensionKeys, Attribute,
Value, CollectedAt` — **no source**, and the value overwrite ignores `collected_at` (that only
feeds `GREATEST(updated_at)`). So there is no precedence layer between the router and the
projection: whatever routes last wins.

**Consequence:** naively re-emitting promoted facts through the ingest path *inverts* today's
guard. A low-confidence promoted value (OS from an HTTP fingerprint, vendor from an OUI/model
guess) would **clobber** an authoritative agent-reported value on any cycle where the promotion
routes after the agent's report — the exact opposite of §10.4.

**Scope of the risk (narrow but real):** only a device observed by *both* an agent and discovery,
*and* only when the two disagree. Discovered-only devices have nothing to clobber; identical
values are harmless.

**Options to actually honor §10.4:**

- **(a) Split the emit by confidence (preferred).** Emit high-confidence intrinsics (hostname,
  friendly_name, model, device_type→kind, service names, identity signals) to their **canonical**
  paths; emit soft inferences (vendor, os) to the **existing** `*_guess` paths
  (`FactPaths.Derived.DeviceVendorGuess`, `DeviceOsGuess`). Reporting already coalesces canonical >
  guess, and last-write-wins *among guesses* is fine. Minimal, and reuses the guess/canonical
  split the codebase already has. Note the model→vendor derivations shipped 2026-07-16 already
  route this way: a device's own SMBIOS model → `DeviceVendorGuess`; a discovered neighbor's model
  → `DiscoveredVendor` (promoted, fill-only). So (a) is already the de-facto pattern.
- **(b) Per-column source precedence in the projection** (agent > promotion regardless of order).
  Heavier: adds a source to `RoutedFact` and a precedence check to every projection's `ON CONFLICT`.
- **(c) A fill-only projection mode** for a dedicated "promotion" source.

**Decision: DEFERRED.** Until Phase 5 is scheduled, promotion keeps its direct fill-only upserts
(correct precedence, but `facts_history` stays incomplete and the All Facts tab stays empty for
discovered-only devices — the original §10.1 gap). Do **not** ship the emit-facts change until one
of (a)/(b)/(c) is chosen, or the "never clobbers" guarantee is knowingly dropped.

## 11. Related, broader problem — derivation batch-clobbering (decided 2026-07-16, not yet built)

> **Directive (Boss, 2026-07-16):** "derivations, in order to be really useful, need to be able to
> run against a set of properties that may or may not be there... we run into the clobbering issue
> only because it's possible to overwrite good data because a derivation landed before we got all
> the data... if we didn't do batching, we could hold all the data in memory, and run the
> derivation confidently with whatever facts we had, and assume the other facts are null."

§10.7 is one instance of a **general** failure mode in `AnalysisEngine`, not a promotion-specific
one. Investigated this session (Fable subagent analysis, reviewed and agreed by Boss) rather than
folded into Phases 1-5 above — this is a separate architectural fix, sequenced after them.

### 11.1 The mechanism (verified)

`AnalysisEngine.Derive` (`src/Core/Analysis/AnalysisEngine.cs:108-143`) indexes derivation inputs
**solely from the current ingest batch** — it never queries `facts_history` or a projection for a
fact that is currently true but wasn't resent this cycle. Agents delta-track (only resend a
changed fact), `RoutedFact` carries no source field, and `GenericProjection`'s `ON CONFLICT`
(`GenericProjection.cs:277`) is last-write-wins per non-null column once routed. Net effect: a
priority-ordered fan-in derivation (e.g. `DeviceVendorDerivation`) only ever sees "present in THIS
batch," not "present in the world." If a low-priority source resends alone in a batch — because
that's the one that changed — while a higher-priority source stays silent (unchanged, so omitted
by delta-tracking), the derivation treats the higher-priority source as **absent**, falls through
to the low-priority one, and overwrites the correct stored canonical value. Priority ordering is
real only *within* a batch; across batches it degenerates to "whichever source most recently sent
a changed value," independent of declared priority.

This breaks **three** derivation shapes, not just priority fan-ins:
1. **Priority fan-ins** (`DeviceVendorDerivation` and siblings) — the clobber described above.
2. **Combinational derivations** (`VendorOsFromDeviceBannerDerivation`) — banner fragments that
   arrive via different collectors in different batches never get combined; the derivation only
   ever sees whichever fragment is in the current batch.
3. **Metric fan-ins** (e.g. `TotalBytes = In + Out`) — silently fail to update when only one
   operand changes in a given batch.

Two existing assets are directly relevant:
- `GenericProjection.EntityStateCache` (`GenericProjection.cs:296-413`, warmed by
  `WarmCacheAsync`, lines 56-115) already solves this **one layer down**: it keeps full per-entity
  state across batches, warm-loaded from the projection table at startup, precisely so a partial
  batch is evaluated against the complete row. The architecture already concedes batch-local state
  is insufficient — just not yet at the derivation layer.
- `facts_history` already stores latest-per-id efficiently (covering-index LATERAL LIMIT 1,
  `FactRepository.cs`), so "current value of path P for device D" is an indexed read away.
- `FactIngestPipeline.cs:92` already flags this as known debt (an "agent-wide post-submission
  derivation pass" deferred to a future ticket).

### 11.2 Options evaluated

- **(a) Hydrate: derive over full current state, not the delta (chosen).** Before `Derive` runs,
  fetch the current value of every derivation input path for the scopes present in the batch,
  merge into the input set, run derivations over the union; subtract the hydrated facts back out
  before append/route so they aren't re-persisted as if newly observed. Recompute is deterministic
  and idempotent — an unchanged canonical is absorbed by history dedup-on-write and
  `EntityStateCache`, so re-emitting it is a no-op. The engine stays stateless in spirit: state is
  passed in as data, not held by the engine.
- **(b) Provenance-aware writes** (§10.7's option (b), generalized: source+rank on `RoutedFact`,
  a precedence guard in every projection's `ON CONFLICT`). **Rejected.** This only stops the wrong
  value from being *stored* — it is still *computed*, and the wrong computed value still reaches
  `facts_history` and `IncidentEvaluator` (the pipeline fans `analyzed` out to all three sinks, not
  just the projection). Only helps priority-select derivations; does nothing for combinational or
  metric fan-ins. Largest blast radius of the options (RoutedFact shape, every projection's SQL,
  per-column rank storage).
- **(c) Sticky decision cache** (Boss's original framing: remember the winning value + winning
  input's priority rank per (device, derivation); a new candidate only overwrites if its rank is
  >= the remembered rank). **Rejected — dominated by (a).** It needs migration semantics whenever
  priorities are reordered or an input is added, and it structurally cannot serve combinational
  derivations (those need the actual input *values* recombined, not a remembered verdict). Caching
  the inputs, as (a) does, subsumes caching a decision about them — Boss's own framing ("hold all
  the data in memory, run the derivation confidently") **is** option (a); the decision cache is the
  more complicated way to approximate it.
- **(d) Full replay from `facts_history` per batch.** Event-sourcing-correct but pointless when a
  compact current-state cache produces identical results at this codebase's scale.

### 11.3 Decision and design sketch

**Decided: option (a).** Add a server-side `DerivationInputState` cache mirroring the existing
`EntityStateCache`/`WarmCacheAsync` idiom (`GenericProjection.cs:56-115`) and the same "re-read
full state" precedent `DiscoveryMaterializer` already uses per pass — scoped to the union of every
`IDerivation.Inputs` path (~30 device-level paths today; a few MB, cheap even at the 80K-device
design target for device-scoped paths). Warm from `facts_history` latest-per-id at startup. The
ingest pipeline hydrates missing inputs before calling `Derive`, then subtracts the hydrated facts
from the output before they're appended/routed, so a hydrated-but-unchanged input is never
mistaken for a newly observed one.

**Accepted tradeoffs:**
1. The first cycle after a server restart degrades to today's behavior until warm-up completes —
   the same accepted tradeoff `WarmCacheAsync` already carries.
2. Don't hydrate high-cardinality paths (per-interface metrics) blindly at the 80K-device target —
   gate hydration per-derivation (e.g. a `HydrateInputs` opt-in flag) or restrict the first cut to
   Device-scoped paths only.
3. This does **not** replace §10.7's guess/canonical path split — that remains a write-precedence
   question between two canonical sources feeding the *same* path, which is a different problem
   from a derivation not seeing all its inputs. But it **composes** with §10.7: once cross-batch
   priority is real, promotion (§10.4) can emit its values as a low-priority *input* to the
   relevant fan-in derivations instead of needing per-column source ranks in the projection — the
   hydrated derivation itself becomes the precedence layer §10.7 is missing, without touching
   `RoutedFact` or projection SQL at all. Revisit §10.7's DEFERRED status once this ships.

**Status:** BUILT (2026-07-16), as Phase 4.5 — sequenced before Phase 5 so promotion can emit its
values as low-confidence derivation inputs and the hydrated derivation becomes the precedence layer
Phase 5 needs. Implementation notes vs the sketch above:
- **No warm cache; per-batch read instead.** `FactRepository.HydrateInputsAsync(devices, paths)`
  reads the current value of the hydratable paths for the batch's devices straight from
  `facts_history` (the existing `(id, collected_at DESC)` covering index — one indexed read per
  batch). At this fleet's scale the read is negligible, and it sidesteps the warm-start/coherency
  concerns a mutable in-memory cache carries. The warm cache remains a possible future optimization
  if profiling ever shows the read matters (it won't at current scale).
- **Engine stays pure.** `AnalysisEngine.Analyze(rawFacts, hydratedInputs)` injects hydrated inputs
  the batch doesn't already carry (batch value always wins), derives over the union, then subtracts
  the injected facts from the output so they are neither re-appended nor re-routed. Prior state is
  passed in as data; the engine holds none.
- **Scope = `AnalysisEngine.HydratableInputPaths`** = `(∪ Inputs − ∪ Outputs)` filtered to
  DimKey == "Device". Derived values are never hydrated (always recomputed); high-cardinality
  per-child paths (interface/filesystem/battery metrics) are excluded by the Device-scope filter —
  the §11.3 tradeoff-2 first cut. `FactIngestPipeline.IngestAsync` hydrates before `Analyze`.
- Covered by `AnalysisEngineTests` (priority fan-in keeps high-priority when only low-priority is in
  the batch; batch value beats stale hydrated; HydratableInputPaths content) and
  `FactIngestPipelineTests.Ingest_LowPriorityVendorAlone_DoesNotClobberStoredHighPriority`
  (end-to-end through the real pipeline).
- **Discovered-scoped fan-ins not yet hydrated** (`VendorFromDiscoveredModelDerivation`,
  `VendorOsFromDiscoveredTypeDerivation` — DimKey "Device|Discovered"): still batch-local. Lower
  cardinality risk than interfaces, but deferred with the Device-scope first cut; revisit if their
  clobbering shows up in practice.
