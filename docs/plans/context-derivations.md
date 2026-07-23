# Context Derivations: cross-batch, cross-entity value resolution

Status: APPROVED 2026-07-23 (Boss) — implementation in progress.

## 1. Problem

Several device-list values are computed at **read time** by `DeviceListApi.cs`'s `BaseCte` as
multi-source "best value right now" picks: friendly_name (3-way coalesce + a lateral
time-ordered pick from `proj_discovered` matched by MAC), mac (lateral newest-`device_fingerprints`
pick), ip (4-source `UNION ALL` ranked by `ip_identity_rank` + recency), last_seen
(`max(device_fingerprints.last_seen)` aggregate). hostname sort on the Components/Interfaces/
Hardware reports joins `proj_systems` from a per-component driving table.

Two costs:

- **No sort can be index-driven.** Verified via EXPLAIN (2026-07-23, PG16): an ORDER BY on a
  LEFT-JOINed (non-driving-table) column is never satisfied by an index on the joined table —
  the planner always falls back to a full `Hash Left Join` + `Sort` (90ms at 150k rows for one
  page of 100). Expression indexes fix only driving-table sort columns (shipped as migration
  0104); everything computed in the CTE or joined in is structurally unfixable at the index layer.
- **The same "best value" logic can't be reused.** The picks exist only as inline SQL in one
  report; `GetDeviceSummary.sql` and others partially duplicate them.

The general capability we want (Boss, 2026-07-23): *"something you can register: these are the
facts/values I need — whatever the most recent one is — and then I'll decide between them."*
This is the derivation framework's contract, but over inputs the framework structurally cannot
see. We expect more uses of this shape over time; this design is the general mechanism, with the
four values above as the first consumers.

## 2. Why the existing derivation framework can't do this (verified)

The engine (`AnalysisEngine`, hydration per §11 of the retired architecture-identity-facts.md —
as-built: per-batch `FactRepository.HydrateInputsAsync` read of Device-scoped inputs from
`facts_history`, batch-wins, injected facts subtracted from output) assumes three things that
each break for one or more of these values:

1. **Same-entity inputs.** `Derive()` runs once per scope group (same `Device=X` key); output ids
   are built from an input fact's own keys (`BuildId`). friendly_name/ip candidates live in
   `proj_discovered` / `proj_device_arp` rows keyed to the **observer** device, matched to the
   subject by MAC. Scope grouping and hydration (`key_values->>'Device' = ANY(batch devices)`)
   structurally cannot see them.
2. **Inputs are facts.** best-mac exists only in `device_fingerprints` (registry table, never in
   `facts_history`); `proj_systems.last_seen_ip` is written only by the raw
   `UpsertDeviceSystem.sql` upsert and is also not a fact. Hydration cannot surface values that
   were never facts.
3. **Recompute triggers on the subject's own batch.** An observer's batch must recompute a
   *subject's* value. The engine runs only over the batch's own devices; agentless
   (discovered-only) devices may never appear as a batch's device at all — materializer-emitted
   facts bypass `AnalysisEngine` entirely (`DiscoveryMaterializer` deliberately never calls
   `Analyze`).

Teaching the engine cross-entity scoping, SQL-backed inputs, and inverse triggers would gut its
purity (Core has no DB access) and contradict the §11 as-built design ("engine stays pure; state
is passed in as data"). We extend *around* it instead.

## 3. Design

Three cooperating pieces. Each extends an existing, proven pattern rather than inventing one.

### 3.1 `IContextDerivation` + `ContextDerivationEngine` (new, Server.Web)

A context derivation mirrors `IDerivation`'s philosophy — declared inputs, pure decide — but its
inputs are **SQL-gathered candidate sets** (tables, any entity, any recency metadata), and it runs
in Server.Web where DB access is legitimate:

```csharp
public interface IContextDerivation
{
    /// Tables whose writes make this derivation's output potentially stale.
    /// Gates the pass, exactly like DiscoveryMaterializer.RelevantTables.
    IReadOnlySet<string> TriggerTables { get; }

    /// One set-based query computing the resolved value for EVERY device in one pass
    /// (not per-device laterals). Returns (device, resolved_value) rows.
    /// The "decide" logic lives here (or in a C# post-pass when SQL is awkward).
    Task<IReadOnlyList<(string Device, string? Value)>> ResolveAsync(
        NpgsqlConnection conn, CancellationToken ct);

    /// The Derived.* fact path the resolved value is emitted under.
    string OutputPath { get; }
}
```

The engine (`ContextDerivationEngine`) runs after `DiscoveryMaterializer` in `FactsEndpoint`'s
post-ingest hook, gated on `touchedTables ∩ TriggerTables` per derivation, with a per-derivation
debounce (min interval, default 30s) since ARP-ish tables are touched by nearly every batch.
For each due derivation it:

1. runs `ResolveAsync` (one set query — at this system's scale, hundreds to low thousands of
   devices, recompute-all is cheaper and simpler than affected-subject fan-out tracking, and it
   makes the mechanism self-healing by construction);
2. filters the resolved rows through an **`EntityStateCache`** (the same change-suppression
   cache `GenericProjection` uses — extracted to its own file and shared; warmed by the startup
   full pass, whose re-emits the downstream dedup layers absorb once) so only genuinely-changed
   values proceed;
3. emits the survivors as subject-keyed `Derived.*` facts via `FactRepository.AppendAsync` +
   `ProjectionRouter.RouteAsync` — the exact precedent of `EmitPromotedIntrinsicsAsync`.

Suppression is thus layered three deep, all existing machinery: the engine's
`EntityStateCache` (step 2), `AppendAsync`'s dedup-on-write for history, and the target
projection's own `GenericProjection` cache + `ON CONFLICT ... WHERE` guard. A steady-state
pass where nothing changed costs one set query and zero writes, and `facts_history` stays a
meaningful change log (a resolution flip is a real, visible event in Device History — a
feature, not a cost).

The **gather** step deliberately has no cache: caching candidate state across passes (observer
ARP rows, fingerprint recency) would re-import the invalidation problem that touched-tables
gating already solves, for negligible gain at current scale. This follows §11's as-built
precedent exactly ("no warm cache; per-batch read instead"); the optimization door stays open
if scale ever demands it.

Set-based recompute-all deliberately dissolves problem #3: there is no trigger-inversion
bookkeeping (which subjects does this observer row affect?) because every pass recomputes every
subject. If scale ever demands it, `ResolveAsync` can gain a candidate-device parameter without
changing the contract.

### 3.2 Outputs are ordinary facts into the existing finals projection

New `FactPaths.Derived.Identity*` constants (`IdentityHostname`, `IdentityFriendlyName`,
`IdentityMac`, `IdentityIp`) route via the normal `ProjectionDef` into **`proj_devices`** —
the projection that already holds derivation finals (vendor/kind/model, all canonical derived
values, none raw). Decided with Boss 2026-07-23: a context derivation *is* a normal derivation
(declared inputs → pure decide → fact output → projection), so its finals belong where
derivation finals already live. A separate `proj_device_identity` table was drafted and
rejected — same mechanics, but it left the existing `vendor` column un-driveable for sorts and
added a Device-dim table for no separation that matters. Deliberately NOT `proj_systems`: those
columns are these derivations' raw *inputs*, and raw-vs-derived stay separated (the
VendorGuess/VendorCanonical rule). Everything downstream is free:

- merge-repoint: automatic (`RepointProjectionsAsync` iterates `ProjectionLibrary.AllDefs`;
  `MergeRepointCoverageTests` passes without changes);
- routing-fitness: new paths have projection columns → `FactPathRoutingFitnessTests` satisfied;
- history: resolution changes are recorded once per actual change;
- self-heal-after-merge: the next gated pass recomputes the survivor (no merge hook needed).

Because context derivations read **tables**, not facts, the three-write-path problem for
hostname/friendly_name (ingest router, `EmitPromotedIntrinsicsAsync`, raw
`UpsertDeviceSystem.sql`) disappears: whichever path wrote `proj_systems`, the next pass sees it.

**Row-presence guarantee**: a device with no resolved values would have no `proj_devices` row
and silently vanish from the driving-table index walk. Every device therefore gets a bare row
at creation (`DeviceRegistry.CreateDeviceAsync`, same transaction) and the engine backfills any
missing rows per full pass (`INSERT ... SELECT FROM devices ON CONFLICT DO NOTHING` — the same
hand-write-into-a-projection precedent as `proj_systems.last_seen_ip`).

### 3.3 Reports drive from the identity projection

Verified via EXPLAIN (2026-07-23, PG16, 50k devices × 3 components):

- Driving `FROM proj_device_identity` (indexed `(coalesce(hostname,''), device)`) with a
  nested-loop join to the per-component table preserves index order: page 1 in **0.28ms**
  (vs 90ms for the join-then-sort shape).
- Keyset page 2 requires the **decomposed cursor** pattern — the naive 3-column row comparison
  spans both tables and can't push into the outer index (49ms); adding a redundant
  outer-table-only prefix (`WHERE (coalesce(i.hostname,''), i.device) >= ($1,$2)` alongside the
  exact 3-column residual) restores the Index Cond: **0.19ms**.

Only the ORDER BY column must live on the driving table; SELECT-list columns can come from any
join. So each report keeps one query template and picks its driving table per sort key:

- Components/Interfaces/Hardware, sort=hostname → drive `proj_device_identity`, nested-loop join
  the component table (decomposed cursor). Other sorts → drive the component table as today
  (0104 expression indexes).
- DeviceListApi: sorts on hostname/friendly_name/ip/mac → drive `proj_device_identity`
  (ip sort via an `ip_sort_key(ip)` expression index — the function is IMMUTABLE, verified);
  sort=last_seen/status → drive `visible_devices` (§3.4); vendor/os sorts stay unindexed
  full-sorts for now (rare sorts, trivial at current scale; add columns to the identity
  projection later if it ever matters — the mechanism is already there).

### 3.4 `last_seen` is not a context derivation (deliberate exception)

`max(device_fingerprints.last_seen)` changes on effectively every resolution — materializing it
as a fact is pure write amplification (the same reason metrics left `facts_history`). Instead:
`devices.last_seen timestamptz`, updated in the same statement/transaction as the existing
unconditional `device_fingerprints.last_seen` stamp in `DeviceRegistry.UpsertFingerprintsAsync`
(one extra row touch on a row already locked), exposed through `live_devices`/`visible_devices`
via `CREATE OR REPLACE VIEW` (Postgres freezes `SELECT *` expansion at view creation — both views
need re-creating in the migration), indexed for the last_seen sort. This also deletes the
aggregate subquery from `BaseCte` and lets `visible_devices`' liveness EXISTS become a plain
column comparison — a second read-path win.

## 4. The four consumers

| Value | TriggerTables | Resolve (set-based, reusing today's read-time logic) |
|---|---|---|
| hostname | proj_systems | copy `proj_systems.hostname` (covers all 3 write paths) |
| friendly_name | proj_systems, proj_discovered, device_fingerprints | `COALESCE(s.friendly_name, best proj_discovered name by newest-mac match with obscured_mac IS NULL guard, s.hostname)` — lift `BaseCte`'s `disc` lateral to a set query |
| mac | device_fingerprints | newest `fp_type='mac'` per device |
| ip | proj_interfaces, proj_systems, proj_device_arp, proj_discovered, device_fingerprints | lift `BaseCte`'s `ip_best` 4-source union + `ip_identity_rank` ordering to a set query |
| last_seen | — (not a context derivation) | §3.4 |

The MAC-scoped candidate joins (friendly_name, ip) keep the `obscured_mac IS NULL` guards and
must respect agent-scoping rules where the source projections carry `agent_id`
(`TracksAgentId` — see AGENTS.md's IP/MAC-join site-scoping incident); the lifted SQL keeps the
exact guards of the `BaseCte` originals, which are the audited reference implementations.

## 5. Bootstrap, staleness, ordering

- **Bootstrap/backfill**: `proj_device_identity` starts empty; the engine runs every derivation
  once at startup (unconditionally — recompute-all is idempotent and cheap; no watermark needed,
  unlike `ProjectionBackfill`).
- **Staleness**: a value only refreshes on a gated pass. Retention-pruning a candidate row does
  not trigger recompute; the value self-heals on the next pass that touches a trigger table
  (frequent in practice — ARP/DHCP are always-send). Same effective semantics as today's
  read-time picks under retention, made explicit.
- **Ordering**: context derivations run after the materializer (they read what it wrote this
  cycle). Chaining context derivations off each other's outputs is out of scope for cut 1
  (none of the four need it); revisit with a topo-sort mirroring `AnalysisEngine` if a consumer
  appears.

## 6. Rejected alternatives

- **Extend `AnalysisEngine` with SQL-backed context inputs**: couples Core to the database,
  breaks engine purity, and still leaves trigger inversion unsolved (agentless devices never
  batch). Rejected.
- **Candidates promoted as facts + existing `PriorityFanInDerivation` decides**: purest layering,
  but the decide only runs inside `Analyze`, which the materializer path deliberately never
  calls — agentless devices would never resolve. Making the materializer run `Analyze` is a much
  bigger behavioral change than this design. Rejected for cut 1; the promotion shape keeps it
  possible later.
- **Fan-out denormalized hostname columns onto each per-component table**: `GenericProjection`
  can't express one-fact→many-rows (`ON CONFLICT` needs the full dim key), and new component rows
  would hold NULL until a hostname re-send that delta-tracking guarantees never comes. The
  drive-from-identity join (§3.3) achieves the same index-driven sort with no new write shape.
  Rejected.
- **DB triggers / periodic full sweeps**: out of codebase pattern; sweep staleness without the
  touched-tables signal. Rejected.
- **Columns on `devices` instead of `proj_device_identity`**: fewer joins, but hand-written
  writes (no `ProjectionDef` machinery), pollutes the registry table with report-serving data,
  and loses automatic merge-repoint/backfill/fitness coverage. `last_seen` is the one deliberate
  exception (§3.4) because its write site is already in the registry. Rejected for the rest.

## 6.5 Store-selection rule (decided with Boss, 2026-07-23)

Where a value lives is chosen by **consumer**, for derivation inputs exactly as for outputs
(the same rule that split proj_discovered's columns from materialization_facts):

- **Joined or sorted by anything** → column projection (`proj_*`). This is why the resolve
  queries read projections and why the finals land on `proj_devices`.
- **Only ever pulled back per (device, path)** → the fact-shaped store. **BUILT (2026-07-23,
  Boss's call — reliability + extensibility over the deferral I recommended):**
  `materialization_facts` is now also the derivation-input current-value store, the durable form
  of §11's sketched `DerivationInputState`. `DerivationInputProjection` (one instance per scope)
  dual-writes every hydratable input there, typed (kind/value_long/value_double — migration
  0107); `FactRepository.HydrateInputsAsync` reads it instead of facts_history's prunable
  latest-per-id, closing the retention-window gap for good; a watermarked backfill
  (`ProjectionBackfill.DerivationInputsWatermark`) replays pre-existing devices' inputs once.
  Retention splits on `entity_key` via `retention_policies.prune_predicate`: device-scoped input
  rows ('' key) are permanent; neighbor-sighting rows prune on the steady tier as before. This
  also closed §11's deferred Discovered-scope gap: `HydratableInputPaths` now includes ALL
  Device|Discovered-scoped inputs (including also-output guard paths — the discovered
  derivations are absence-guarded gap-fills, and batch-wins + injected-subtraction makes
  hydrating a guard path safe), so a delta-tracked model-only batch can no longer clobber an
  observed vendor (regression: `Ingest_DiscoveredModelAlone_DoesNotClobberObservedVendor`).
  Adding a derivation input is now a data change — a routed path — never a column migration.
- **Exceptions that read where they authoritatively live**: cross-entity resolution
  (subject-by-MAC over observer rows), registry state (`device_fingerprints`), and
  never-was-a-fact writes (`proj_systems.last_seen_ip`).

## 7. Alignment with the §11 decision

§11 (retired doc, recovered from git; as-built 2026-07-16) fixed *cross-batch* clobbering for
*same-entity fact* inputs via hydration, keeping the engine pure. This design is the same move
one layer out: *cross-entity/non-fact* inputs, resolved by a pure decide over state passed in as
data, living where DB access already exists. Hydration, `Analyze`, and every existing derivation
are untouched.

## 8. Implementation phases

1. `devices.last_seen` + view re-creation + `UpsertFingerprintsAsync` stamp + index + swap
   `BaseCte`/`visible_devices` aggregate reads (self-contained, ships alone).
2. `IContextDerivation`, engine, debounce, startup pass; hostname + mac derivations;
   `proj_device_identity` ProjectionDef + FactPaths; integration tests (resolve → projection →
   merge-repoint coverage).
3. friendly_name + ip derivations (lift the `BaseCte` laterals; keep guards verbatim).
4. Report query restructure: per-sort-key driving table + decomposed cursor in
   Components/Interfaces/Hardware/DeviceList; EXPLAIN-based integration test asserting index use
   (closes the COMP-009 "assert EXPLAIN shows index use" gap that was never implemented).
5. Delete the now-dead `BaseCte` laterals.

DatabaseCommand generator work (separate effort) sequences after phase 4: with sort expressions
reduced to plain driving-table columns, the `[Sortable]` extension shrinks to column+direction
variants — the complexity it would have had to encode is resolved here instead.
