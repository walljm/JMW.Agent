---
agent: sdev-architecture
date: 2026-07-16
status: in_review
mode: standalone
---

# Unified Operator-Authored Device Facts — Architecture

> Companion to `docs/plans/user-provided.md` (requirements, approved) and
> `docs/plans/ux-operator-facts.md` (UX, approved). This is the architecture pass those documents
> deferred: API shapes, storage layout, the override-vs-arbitrary disambiguation + near-miss
> algorithm, the exhaustive NFR-8 identity-bearing exclusion list, the Custom Fields migration
> mechanics, and validation rules.
>
> **Run mode:** standalone. `planning/` exists but is empty and unused for this feature — the
> requirements and UX passes both wrote single structured documents under `docs/plans/`, and this
> document follows that established pattern rather than the `planning/architecture/` per-record
> tree. Every claim below about the fact pipeline, storage schema, projection routing, and the
> discovery materializer was verified by reading the primary source this session, not inferred.
>
> **Product decisions folded in (Boss, 2026-07-16):** (1) NFR-8 excludes **both** tiers (identity +
> promotion) from override — the conservative default. (2) REQ-011's "description/type" wording is
> accepted-corrected: `custom_field_definitions` stores only a `label` today, so the "Type hint
> badge" from UX §2.1 is dropped (no data feeds it). (3) The UX doc's stale `DEC-9`/`DEC-11`
> cross-references map to `DEC-8` (free-form arbitrary scope) and `REQ-003` (near-miss); a one-line
> fix in `ux-operator-facts.md` is recommended, non-blocking.

---

## 1. Design thesis

This feature is a **strict generalization of existing infrastructure**, not new plumbing.
`facts_history` already stores arbitrary path strings with multi-dimension keys, and
`FactSource.ManualEntry` is already the one source permitted to be hard-deleted. Both mechanisms
being replaced (Manual Overrides, Custom Fields) already write through this exact pipeline.

Therefore the architecture is deliberately small:

1. **No storage change for fact values.** Overrides of any dimensionality, and arbitrary facts, are
   ordinary `ManualEntry` rows in `facts_history` (§3.1).
2. **One new table** — path-level label/description metadata (§3.2), the only thing arbitrary facts
   can't self-describe.
3. **A write-time gate** — disambiguation (override vs. arbitrary), the NFR-8 identity exclusion,
   near-miss detection, and overwrite/revert confirmation (§4, §5, §6).
4. **Read queries** — per-device tab, fleet-wide browse, child-collection key suggestions (§7).
5. **Retire Custom Fields** — migrate its label metadata, delete the constant + subsystem (§8).

## 2. Fact-identity model (verified: `src/Core/Fact.cs`, `src/Core/FactSegment.cs`)

- `Fact.Create(template, keys[], value)` fills `[]` placeholders left-to-right (`FillKeys`).
  - `Id` = the literal path with keys substituted, e.g. `Device[<uuid>].Interface[<mac>].SpeedBps`.
    It is **not** a hash.
  - `AttributePath` = the template with empty brackets, e.g. `Device[].Interface[].SpeedBps`.
  - `KeyValuesJson` = `{"Device":"<uuid>","Interface":"<mac>"}` — written in path order, deterministic
    (canonical).
  - `DimKey` = pipe-joined list-segment names, e.g. `Device|Interface`.
- **`FillKeys` already throws `ArgumentException` when key count ≠ bracket count** — the native
  key-count validation the requirements ask for (REQ-001, REQ-002).
- **`FactSegment.ParsePath` already enforces `MaxIdLength=512` and `MaxSegments=32`** before any
  allocation, throwing `ArgumentOutOfRangeException` — the native path-bounds validation (REQ-002).

**Consequence — disambiguation is a pure function of the template (REQ-003).** A submitted path is
an **override** if and only if its `AttributePath` template equals a declared `FactPaths` constant;
otherwise it is an **arbitrary** fact. Because `Fact.Id` is literally the path (not a hash), a given
`(device, path, key)` maps to exactly one row `id` — the same tuple can never be represented by two
disconnected mechanisms. This structurally satisfies REQ-003's core constraint.

## 3. Storage layout

### 3.1 `facts_history` — unchanged (verified: migrations `0001`, `0053`)

Columns: `id TEXT`, `attribute_path TEXT`, `key_values JSONB`, `kind SMALLINT`, `value_str`,
`value_long`, `value_double`, `collected_at TIMESTAMPTZ`, `source SMALLINT`, `source_name TEXT`.
PK `(id, collected_at)`; latest-value-per-id served by `facts_history_id_time_idx (id, collected_at
DESC) INCLUDE (kind, value_str, value_long, value_double)`.

Every operator-authored fact — override (any scope) or arbitrary — is a `ManualEntry` (`source = 2`)
row, written exactly as the two current mechanisms already write. `source_name = "user:<name>"` (the
existing convention) **is** the audit trail (NFR-1) — no separate audit-log concept. **No value-row
migration is required.**

### 3.2 New table: `fact_path_metadata` (REQ-011)

Path-level label/description, **device-independent** (confirmed UX §6.5.3: keyed by path alone, not
by `(device, path)`).

```sql
CREATE TABLE fact_path_metadata (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    attribute_path TEXT  NOT NULL,               -- template, e.g. 'Device[].Custom[].Value'
    key_values     JSONB NOT NULL DEFAULT '{}',  -- NON-device keys only, e.g. {"Custom":"warranty-expiry"}
    label          TEXT,
    description    TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by     TEXT NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by     TEXT,
    UNIQUE (attribute_path, key_values)
);
```

The metadata key is the fact's **device-independent identity**: its `attribute_path` plus its
`key_values` with the `Device` entry removed.

- Custom field `warranty-expiry` → `('Device[].Custom[].Value', {"Custom":"warranty-expiry"})` — one
  row per field, shared across every device (device-independent).
- Device-scoped arbitrary fact `Device[].Rack.Position` → `('Device[].Rack.Position', {})`.
- `key_values` is canonicalized (path order, by `Fact.ComputeKeyValuesJson`) so the `UNIQUE
  (attribute_path, key_values)` index is reliable — Postgres compares `jsonb` by normalized value,
  and the producer always emits keys in the same path order, so two equivalent keys never collide.

**"Path alone" clarification (UX §6.5.3, cycle-2 answer to critic S3).** "Keyed by path alone, not
`(device, path)`" means *device-independent* — the metadata key deliberately strips the `Device`
entry. For a custom field the slug is part of the field's identity, so it is retained in `key_values`
(`{"Custom":"warranty-expiry"}`) — that is what makes labels per-field rather than collapsing every
slug into one row. For a child-collection-scoped arbitrary fact (`Device[].SwitchPort[Gi0/1].Label`)
the retained non-device key makes the label **per child-instance**. This is the correct generic
behavior: a per-instance label is device-independent (satisfying §6.5.3) and the operator may still
label instances uniformly; a template-only key would be wrong for the custom-field case, which is the
one the requirement is actually about. Labels on override paths are unusual but follow the same rule.

**Accepted correction to REQ-011 (Boss-confirmed):** `custom_field_definitions` has only `label` —
no `description`, no `type` column (verified migration `0074`). Migration carries `label`;
`description` is new and starts NULL; there is no `type` to migrate. The UX "read-only Type hint
badge" is dropped — no data feeds it, and a `value_type` column now would be gold-plating.

## 4. NFR-8 — exhaustive identity-bearing exclusion list

`DiscoveryMaterializer` (verified end to end, `src/Server.Web/Ingest/DiscoveryMaterializer.cs` plus
every `Data/Discovery/*.sql` it calls) **never reads `facts_history`** — it reads **projection
columns**. Operator overrides **do** flow into projection columns (same `FactIngestPipeline` as
collectors — requirements §2, `ManualFactCatalog` doc comment). So an override of a column the
materializer consumes as a fingerprint or promotion input can corrupt agentless device resolution.

The exclusion is therefore **column-precise, not table-level.** A table-level rule would wrongly
block `InterfaceSpeedBps` (routes to `proj_interfaces`), which NFR-8's own acceptance criteria
require to remain overridable.

### 4.1 The list (both tiers blocked — 29 constants + `HwSystemSerial`)

Each row is a `FactPaths` const whose projection column a materializer query reads as an input,
with the query that reads it. Mapping verified against `ProjectionLibrary.cs`.

**Tier 1 — identity/merge-critical** (wrong value ⇒ wrong device merge or spurious duplicate):

| Const | Projection column | Read by |
|---|---|---|
| `ArpMac` | `proj_device_arp.mac` | GetNewArpMacs, GetKnownMacsForIp |
| `DiscoveredMAC` | `proj_discovered.mac` | GetNewDiscoveredMacs, GetKnownMacsForIp |
| `DiscoveredObscuredMAC` | `proj_discovered.obscured_mac` | GetObscuredMacRows |
| `DiscoveredOnvifSerial` | `proj_discovered.onvif_serial` | GetNewDiscoveredSerials, GetPromotionGapRows |
| `DiscoveredRokuSerial` | `proj_discovered.roku_serial` | GetNewDiscoveredSerials, GetPromotionGapRows |
| `DiscoveredSnmpSerial` | `proj_discovered.snmp_serial` | GetNewDiscoveredSerials, GetPromotionGapRows |
| `DiscoveredSsdpUuid` | `proj_discovered.ssdp_uuid` | GetNewDiscoveredSerials, GetPromotionGapRows |
| `DiscoveredWsdUuid` | `proj_discovered.wsd_uuid` | GetNewDiscoveredSerials, GetPromotionGapRows |
| `DiscoveredSshHostKey` | `proj_discovered.ssh_host_key` | GetSshHostKeyRows |
| `DiscoveredHueBridgeId` | `proj_discovered.hue_bridge_id` | GetScannerIdRows |
| `DiscoveredOnvifHardwareId` | `proj_discovered.onvif_hardware_id` | GetScannerIdRows |
| `DiscoveredCastId` | `proj_discovered.cast_id` | GetObscuredMacRows, GetCastIdIpCounts |
| `InterfaceMAC` | `proj_interfaces.mac_address` | GetInterfaceObscuredMacRows |
| `InterfaceObscuredMAC` | `proj_interfaces.obscured_mac` | GetInterfaceObscuredMacRows |
| `InterfaceIPv4` | `proj_interfaces.ipv4` | GetInterfaceObscuredMacRows (MAC-reconstruction join key) |
| `DhcpLocalLeaseIP` | `proj_dhcp_local_leases.ip` | GetNewDhcpLocalMacs, GetKnownMacsForIp |

**Tier 2 — promotion inputs** (wrong value ⇒ wrong/suppressed promoted metadata; not a merge —
blocked per Boss's conservative default):

| Const | Projection column | Read by |
|---|---|---|
| `DiscoveredHostname` | `proj_discovered.hostname` | GetPromotionGapRows, discovered-source promote |
| `DiscoveredFriendlyName` | `proj_discovered.friendly_name` | GetObscuredMacRows, GetPromotionGapRows |
| `DiscoveredVendor` | `proj_discovered.vendor` | discovered-source promote, GetPromotionGapRows |
| `DiscoveredModel` | `proj_discovered.model` | discovered-source promote, GetPromotionGapRows |
| `DiscoveredOs` | `proj_discovered.os` | discovered-source promote, GetPromotionGapRows |
| `DiscoveredDeviceType` | `proj_discovered.device_type` | GetObscuredMacRows |
| `HwSystemVendor` | `proj_hardware.system_vendor` | GetPromotionGapRows (gap detection) |
| `HwSystemModel` | `proj_hardware.system_model` | GetPromotionGapRows (gap detection) |
| `SystemHostname` | `proj_systems.hostname` | GetPromotionGapRows (gap detection, `:42`) |
| `SystemOsFamily` | `proj_systems.os_family` | GetPromotionGapRows (gap detection, `:41`) |
| `SystemFriendlyName` | `proj_systems.friendly_name` | GetPromotionGapRows (gap detection, `:43`); promoted `DiscoveryMaterializer.cs:110` |
| `DhcpLocalLeaseHostname` | `proj_dhcp_local_leases.hostname` | GetPromotionGapRows (fallback) |

**Plus `HwSystemSerial`** — kept excluded per Boss's confirmed list. Honest caveat: the materializer
does **not** read `proj_hardware.serial` (gap detection reads only `system_vendor`/`system_model`).
Its identity role is the **agent-direct** chassis-serial fingerprint, not agentless materialization.
Excluding it is safe and matches the confirmed decision; the justification is "serial is a
fingerprint on the agent path," not "the materializer reads it."

### 4.2 Explicitly NOT excluded (remain overridable — verified)

`InterfaceSpeedBps` and every other non-identity `Interface*`, `InterfaceIPv4PrefixLength` (routes to
`proj_interfaces.ipv4_prefix_length`, which the materializer does not read — it reads `ipv4`),
`SshInterfaceIP` (`Device[].Interface[].IP` — no mapping to `proj_interfaces.ipv4`), `DiscoveredSources`,
`SystemOsDistro`, `DhcpLocalLeaseExpires`/`Source`, and all disk/docker/security/battery/etc. facts.
This satisfies NFR-8 AC's adjacency requirement (an override of `InterfaceSpeedBps`, next to
`InterfaceMAC`, succeeds normally).

> **Cycle-2 correction:** cycle 1 wrongly listed `SystemFriendlyName` here as "not read." It maps to
> `proj_systems.friendly_name` (added in migration `0082`), which `GetPromotionGapRows.sql:43` reads
> for gap detection and `DiscoveryMaterializer.cs:110` promotes — the identical pattern to
> `SystemHostname`/`SystemOsFamily`. It is now in Tier 2 (§4.1). This miss is exactly the
> false-negative failure mode the §4.3 completeness test is redesigned to catch.

`proj_dhcp_leases` is **Service-scoped** (`ServicePaths.DhcpLease*`, dims `[Service, Scope, Lease]`).
The unified mechanism is device-scoped (`Device[]` root), so Service paths are out of scope entirely
— as they are today — and need no device-const exclusion even though the materializer reads that
table.

### 4.3 Single documented artifact + completeness fitness test (NFR-8 AC #3, #4)

- Declare `IdentityBearingFactPaths` — a `HashSet<string>` co-located with the (generalized)
  `ManualFactCatalog`, with the §4.1 table as its doc comment (the "single, discoverable, documented
  artifact").
- Declare, **co-located with the queries in `DiscoveryMaterializer`**, an explicit
  `IdentityInputColumns` manifest: the set of `(table, column)` pairs the passes read as fingerprint
  or promotion inputs, each annotated with the reading query. This lives next to `RelevantTables`
  (which already exists at the table grain) and is the column-grain companion.
- **Completeness fitness test (redesign — answers critic B3).** The cycle-1 design only asserted the
  list was a *subset* of materializer-adjacent columns (catches false positives / bogus entries) —
  it structurally could not catch a *missing* exclusion, and cycle-1's `SystemFriendlyName` miss
  proved that gap. The redesign is an **exact set-equality** check:
  ```
  expected = IdentityInputColumns
      .Select(map (table,column) → FactPaths const via ProjectionLibrary)   // dimension-KEY columns
      .Where(mapped)                                                        // don't map → dropped
  Assert.Equal(expected ∪ { HwSystemSerial }, IdentityBearingFactPaths)
  ```
  Exact equality fails on **both** directions: a materializer read newly added to
  `IdentityInputColumns` but missing from `IdentityBearingFactPaths` (false negative — the B2 class),
  and a const in the list with no backing read column (false positive). `HwSystemSerial` is the one
  documented exception (agent-path fingerprint, not a materializer read — §4.1), asserted explicitly
  so it can't silently mask a real miss.
- **Residual risk, stated honestly:** `IdentityInputColumns` is hand-maintained — full automation
  would require parsing the `.sql` files' SELECT lists, which isn't worth it. But it is a single
  co-located checklist next to the queries, and the exact-equality test forces it and the exclusion
  list to move together. A code-review rule ("adding a `DiscoveryMaterializer` read ⇒ update
  `IdentityInputColumns`") backs it. A cross-check asserts `IdentityInputColumns`'s tables ⊆
  `RelevantTables`. This is the best achievable completeness check short of SQL parsing, and unlike
  the subset-only check it is a real completeness guard.
- **AC tests:** every excluded const is rejected at write time; `InterfaceSpeedBps` (adjacent to
  `InterfaceMAC`) succeeds.

## 5. Write path — API

All endpoints join the existing `/api/v1/admin` route group (verified `Program.cs:418` — already
`RequireAuthorization(RbacPolicies.Admin)` + a CSRF endpoint filter on state-changing methods,
satisfying NFR-2). `OperatorFactsApi` replaces `DeviceFactsApi` + `CustomFieldsApi`.

```
POST   /api/v1/admin/devices/{id:guid}/operator-facts        set / override / create
DELETE /api/v1/admin/devices/{id:guid}/operator-facts        revert / clear
GET    /api/v1/admin/devices/{id:guid}/operator-facts        device-detail tab data
GET    /api/v1/admin/fact-catalog                            overridable catalog + excluded set (combo box)
GET    /api/v1/admin/devices/{id:guid}/collection-keys?dim=Interface   child-key suggestions (REQ-010)
GET    /api/v1/admin/operator-facts?path=<template>&cursor=… fleet-wide, path-scoped (REQ-006)
GET    /api/v1/admin/operator-facts/paths?cursor=…           fleet-wide "browse all paths" (UX §2.5)
PUT    /api/v1/admin/operator-facts/metadata                 path-level label/description (UX §6.5.3)
```

### 5.1 `POST` request

```jsonc
{
  "attribute_path": "Device[].Interface[].SpeedBps", // template (from combo) OR free-form
  "keys": ["aa:bb:cc:dd:ee:ff"],                     // NON-device keys, path order (device from route)
  "value": "1000000000",
  "label": null,          "description": null,       // optional → fact_path_metadata
  "confirm_arbitrary": false,                         // bypass near-miss warning (REQ-003)
  "confirm_overwrite": false                          // bypass existing-value confirmation (REQ-005)
}
```

### 5.2 Server algorithm

1. **Parse & bound-check** `attribute_path` via `FactSegment.ParsePath` → on overflow, `400` "Fact
   IDs are limited to 512 characters / 32 segments" (REQ-002). **Require the first segment to be a
   *list* segment named `Device` (`IsList && Name=="Device"`)** — not merely named `Device` (critic
   S1). This rejects malformed submissions like `Device.Interface[].X` (device as a scalar) that
   would otherwise misroute, and it rejects `Service[]`-rooted paths (out of scope).
2. **Key-count check:** `keys.length == bracketCount(template) - 1` (device is the implicit first
   key, from the route) → else `400` naming the mismatch (REQ-001/002). Holds for the device-only
   case too: `Device[].Rack.Position` has `bracketCount == 1`, so `keys == []`. (Redundant with
   `FillKeys`, but produces a clean domain error instead of an exception.)
3. **Classify:**
   - `template ∈ IdentityBearingFactPaths` → `422` `{error:"identity_protected", path}` (NFR-8,
     REQ-001).
   - `template ∈ FactPaths.Derived` **or** `template ∈ FactPaths.MetricPaths` → `422`
     `{error:"not_authorable", path}` (critic S2): Derived facts are recomputed by the analysis
     pipeline from other facts (a manual value would be clobbered on the next cycle and, worse, would
     be mislabeled "arbitrary/unprojected" when it actually routes to `proj_devices` etc.); metric
     paths route to `metrics_raw` as monotonic counters, where a hand-authored value is meaningless.
     This mirrors the exclusions the current `ManualFactCatalog` already applies.
   - `template ∈ FactPaths` catalog (any dimensionality; minus identity/Derived/Metric/Service) →
     **override**.
   - else run near-miss (§6): a unique catalog near-match and `!confirm_arbitrary` → `409`
     `{warn:"near_miss", suggestion:"<catalog template>"}`. Otherwise → **arbitrary**.
4. **Overwrite guard (REQ-005):** if a `ManualEntry` row already exists at this fact id and
   `!confirm_overwrite` → `409 {warn:"overwrite"}`.
5. **Write:** `Fact.Create(attribute_path, [deviceId, ...keys], value) with { Source = ManualEntry,
   SourceName = "user:" + name }`; `FactIngestPipeline.IngestAsync`; if `label`/`description`
   present, upsert `fact_path_metadata` (keyed by `(attribute_path, key_values − Device)`);
   `AuditLog.WriteAsync`.

### 5.3 `DELETE` (revert / clear, REQ-005)

Compute the fact id from `path` + keys; call the existing `DeleteManualFactByIdAsync(id,
FactSource.ManualEntry)` — source-scoped, so it can only remove `ManualEntry` rows and never touches
collector history for the same or any other path. `204` if a row was removed, `404` otherwise.
Confirmation is client-side (`confirm()`, UX FLOW-004). Reverting reveals the underlying
collector-observed value automatically (for override paths, because the projection reverts to the
collector's last value on the next relevant write; the value shown on the device page is the
latest-per-id fact, which after delete is the collector's row).

### 5.4 Error contract

All errors use the existing `ApiError`/`Problem` shape (uniform across every admin endpoint). No new
error format is introduced. Every documented rejection (identity-protected, wrong key count,
oversized path, near-miss, overwrite) is an explicit, admin-visible response — never a silent no-op
(NFR-6).

## 6. Near-miss matching (REQ-003) — advisory

Purely advisory: the operator can always proceed with either interpretation.

- Candidate set = all `FactPaths` catalog templates.
- Normalize submitted template and each candidate: strip leading `Device[].`, lowercase, remove
  brackets/punctuation.
- Levenshtein distance on the normalized strings; keep candidates within `max(2, ceil(0.15 × len))`.
- **Warn iff exactly one** candidate qualifies (return it as `suggestion`); several or zero ⇒ treat
  as genuinely arbitrary.
- A dimensionality typo — e.g. `Device[].Interface.SpeedBps` with the `[]` missing — normalizes to
  the same string as the real catalog path (distance 0) → correct single suggestion. This is the
  common real case.

In-memory over ~450 short strings; no dependency; runs per submit (NFR-5/NFR-7 respected).

## 7. Read paths

### 7.1 Device-detail "Operator Facts" tab (SCR-001)

New query `GetDeviceOperatorFactsAsync(deviceId)`: latest-value-per-id
(`DISTINCT ON (id) … ORDER BY id, collected_at DESC`) where `key_values->>'Device' = deviceId AND
source = 2`, `LEFT JOIN fact_path_metadata` on `(attribute_path, key_values − 'Device')`. Returns per
row: path, scope key(s), value, `label`, `source_name`, `collected_at`, and a computed **kind**
(`Override` if `attribute_path ∈ catalog`, else `Arbitrary`).

This is a **direct query, not a fact view** — it decouples the management UI from the
projection/view system entirely. (Overrides of catalog paths still appear in their normal fact views
automatically, because the override updates the projection column; that is the override *taking
effect*, distinct from this management table.)

**Index note (critic S4):** this per-device query needs no new index. Every fact id for a device is
prefixed `Device[<deviceId>]…`, so it is served by a range scan on the existing
`facts_history_id_time_idx (id, …)` (`id >= 'Device[<id>]' AND id < 'Device[<id>]' || high-sentinel`),
with the `source = 2` predicate applied as a cheap post-filter over the device's own (small) fact
set. The new partial index in §7.2 exists only for the fleet-wide `attribute_path`-keyed queries,
which the id-prefix index cannot serve. Per-device volume is tiny (NFR-5), so this is comfortable.

### 7.2 Fleet-wide browse (REQ-006, SCR-002)

Both modes keyset-paginated (project mandate — never offset/limit):

- `operator-facts?path=<template>`: latest-per-id where `attribute_path = <template> AND source = 2`;
  one row per `(device, non-device keys)`; cursor `(key_values->>'Device', id)`. Answers "which
  devices have a value for fact X."
- `operator-facts/paths`: distinct `attribute_path` (+ non-device key signature) among `ManualEntry`
  rows, with device counts and `LEFT JOIN` metadata label; cursor `attribute_path`. This is UX
  §2.5's "browse all paths," preserving Custom Fields' "see what fields exist" capability.

**New index** to serve both without scanning: partial index
`facts_history (attribute_path, id) WHERE source = 2`. Justified — REQ-006 is a cross-device scan
over the source-filtered subset, which the existing `(id, collected_at DESC)` index does not front.

### 7.3 Child-collection key suggestions (REQ-010)

`collection-keys?dim=Interface` → `SELECT DISTINCT key_values->>'<dim>' FROM facts_history WHERE
key_values->>'Device' = deviceId AND key_values ? '<dim>'`. Works for any dimension — catalog or
arbitrary — with no per-dimension projection knowledge. Unmatched typed keys are accepted downstream,
never rejected (REQ-010 AC / DEC-6). For the common `Interface` case, the query can self-join the
device's `Interface[].Name` facts to show interface names next to MACs (UX FLOW-002).

## 8. Custom Fields migration & retirement (REQ-004/007/008/009/011)

**Values: zero row rewrites.** Custom-field values already live at
`Device[<id>].Custom[<slug>].Value` as `ManualEntry` facts. They remain valid operator-authored facts
under the unified mechanism, so value preservation (REQ-007) is trivially guaranteed — the migration
touches no value row (REQ-009: no extra outage).

**Delete the `FactPaths.CustomFieldValue` constant.** This resolves a real constraint verified in
`FactPathRoutingFitnessTests.cs`: `Device[].Custom[].Value` is kept green today **only** by the
baseline Custom Fields fact view. Retiring that view (which REQ-004 requires) would turn the const
red. Deleting the const is the clean resolution, and every consequence is desirable:

- Legacy `Device[<id>].Custom[<slug>].Value` rows stay in `facts_history` untouched and now classify
  as **arbitrary** facts (which is exactly what they are) — rendered in the Operator Facts tab,
  inherently unprojected (NFR-4).
- The fitness test only inspects declared constants, so removing the const removes it from the test
  (no red).
- Remove `CustomFieldViewMerger`, `FactViewLibrary.CustomFieldsViewTitle` + the baseline view,
  `CustomFieldsApi`, the `custom_field_definitions` queries, `Pages/Admin/CustomFields.cshtml*`, and
  `DeviceFactsApi.SetCustomFieldValue`/`ClearCustomFieldValue`.

**Metadata migration** — one transactional forward migration `0083_operator_facts.sql`:

```sql
INSERT INTO fact_path_metadata (attribute_path, key_values, label, created_at, created_by)
SELECT 'Device[].Custom[].Value', jsonb_build_object('Custom', slug), label, created_at, created_by
FROM custom_field_definitions;
DROP TABLE custom_field_definitions;
```

`target_view_title` / `target_view_group` / `is_new_view` are **not** migrated — view placement is
retired with the merged-view rendering (UX §3 replaces it with the flat tab; it was never a
requirement).

### 8.1 Migration Strategy (brownfield — `core.home` in production)

- **Approach:** single forward migration, transactional. No blue-green/rolling needed (single
  server, single admin operator). **Numbering (critic B1):** the migration tip on disk is
  `0082_proj_systems_friendly_name.sql`, so the next free number is **`0083`** — `0081` and `0082`
  already exist. (Cycle 1 said `0081` because an earlier `ls | head -80` truncated the listing at
  `0080`; corrected.) The implementer must re-check the tip at implementation time in case more
  migrations land first.
- **Steps:** (1) create `fact_path_metadata`; (2) create the partial `facts_history` browse index;
  (3) backfill metadata from `custom_field_definitions`; (4) `DROP TABLE custom_field_definitions`.
  Run the migration **before** the code that removes the Custom Fields subsystem (requirements §11 —
  label metadata is unrecoverable from `facts_history` once the table is dropped).
- **Rollback:** reversible in principle (recreate `custom_field_definitions` from
  `fact_path_metadata WHERE attribute_path = 'Device[].Custom[].Value'`). Since no value row was ever
  moved, a code-only rollback leaves all data intact.
- **Validation (REQ-008):** assert `count(migrated fact_path_metadata) == count(custom_field_definitions)`
  before the drop; value rows are demonstrably unchanged (0 touched). Integration test against real
  Postgres asserting metadata count parity and that every pre-existing `Custom[<slug>].Value` fact is
  still readable through the new device query with its migrated label.

## 9. Component / file map

**New:**
- `IdentityBearingFactPaths` + a generalized overridable catalog (replaces `ManualFactCatalog`'s
  single-list-dimension filter; still excludes `Derived`/`MetricPaths`/`ServicePaths`, now also
  identity paths).
- `OperatorFactsApi` (replaces `DeviceFactsApi` + `CustomFieldsApi`).
- Near-miss matcher (small static helper).
- `fact_path_metadata` typed queries; `GetDeviceOperatorFactsAsync`; fleet `operator-facts` +
  `operator-facts/paths` queries; `collection-keys` query.
- Migration `0083_operator_facts.sql`.
- `Pages/Admin/OperatorFacts.cshtml*` (replaces `CustomFields.cshtml*`, same `HubTabSets.Data` slot,
  route `/admin/operator-facts`).
- `.combo` / `.combo-results` / `.combo-option` CSS + shared combo-box JS (UX §4).

**Modified:**
- `Pages/Reports/DeviceDetail.cshtml` — "Manual Overrides" tab → "Operator Facts"; remove the
  custom-field markup + JS (`:805`, `:816`, `:819`, `:1056`, `:1073`) in favor of the unified tab.
- `Pages/Reports/DeviceDetail.cshtml.cs` — remove the `CustomFieldValues` property (`:98`) and its
  population that reads `FactPaths.CustomFieldValue` (`:453`, `:456`); bind the tab to the new direct
  query instead.
- `src/Server.Web/FactViews/FactViewLibrary.cs` — remove the baseline Custom Fields view and its
  `FactViewColumn.Fact("Value", FactPaths.CustomFieldValue)` (`:20` comment, `:33`) and
  `CustomFieldsViewTitle`.
- `Program.cs` — swap endpoint wiring (`DeviceFactsApi`/`CustomFieldsApi` → `OperatorFactsApi`).
- `src/Core/Analysis/FactPaths.cs` — delete the `CustomFieldValue` constant (`:23`).
- `FactPathRoutingFitnessTests` (const removed — no other change needed) + new NFR-8 completeness
  fitness test (§4.3).

**Complete `CustomFieldValue` reference set to resolve (critic B4 — grepped `src` + `test`):**
`FactPaths.cs:23` (const, delete), `FactViewLibrary.cs:20,33` (view, delete),
`CustomFieldViewMerger.cs:12,40` (delete file), `DeviceFactsApi.cs:34,36,114–187` (delete endpoints),
`DeviceDetail.cshtml:805,816,819,1056,1073` (delete markup/JS),
`DeviceDetail.cshtml.cs:98,453,456` (remove property + population),
`test/Unit/Server/CustomFieldViewMergerTests.cs:43` (delete with the merger). No reference is left
dangling by the const deletion.

**Deleted:** `CustomFieldViewMerger` (+ `CustomFieldViewMergerTests`), the baseline Custom Fields
fact view + `CustomFieldsViewTitle`, `custom_field_definitions` table + its CRUD queries in
`ManualFactQueries` (`InsertCustomFieldDefinitionAsync`, `ListCustomFieldDefinitionsAsync`,
`GetCustomFieldDefinitionBySlugAsync`, `DeleteCustomFieldDefinitionAsync`,
`DeleteManualFactsByCustomSlugAsync`), the `CustomFieldDefinition` model, `CustomFieldsApi`, the
custom-field endpoints on `DeviceFactsApi`, and `Pages/Admin/CustomFields.cshtml*`.

## 10. Architectural decisions

- **ADR-1 — Reuse `facts_history` for all operator facts; no value migration.** Alternative
  (dedicated operator-facts table) rejected: it forks the very concept the feature unifies and
  duplicates audit/revert semantics already present on `ManualEntry`.
- **ADR-2 — Disambiguate by template-vs-catalog identity.** `Fact.Id` uniqueness prevents dual
  representation. A separate `kind` flag column rejected — redundant with the path.
- **ADR-3 — NFR-8 as a column-precise curated set + a materializer-tied fitness test.** Read-side
  provenance tracking rejected (requirements DEC-4 already rejected it — too invasive); table-level
  exclusion rejected (blocks `InterfaceSpeedBps`, violating NFR-8 AC).
- **ADR-4 — Delete `CustomFieldValue`; legacy values become arbitrary facts.** A retained stub view
  rejected — it would preserve the concept REQ-004 retires and keep the routing coupling.
- **ADR-5 — Metadata keyed by `(attribute_path, non-device key_values)`.** Template-only keying
  rejected — it collapses all custom-field slugs to a single metadata row.

## 11. Verification & test plan (maps to requirements §13)

- **Unit:** disambiguation (override vs. arbitrary vs. near-miss); key-count validation (incl. the
  device-only `keys == []` case); path-bounds rejection; first-segment-must-be-`Device[]`-list
  rejection (S1); Derived/Metric-path rejection (S2); NFR-8 rejection for every excluded const +
  `InterfaceSpeedBps` success; near-miss single-candidate rule (incl. the missing-`[]` case).
- **Fitness (completeness):** exact set-equality of `IdentityBearingFactPaths` with the consts mapped
  from `DiscoveryMaterializer.IdentityInputColumns` ∪ `{HwSystemSerial}` (§4.3) — catches both a
  missing exclusion and a bogus one; plus `IdentityInputColumns` tables ⊆ `RelevantTables`.
- **Integration (real Postgres):** Custom Fields migration count parity + value/label survival;
  revert/clear source-scoping (never deletes collector rows); an NFR-8 test that overrides an
  excluded const and confirms the projection column — and thus `DiscoveryMaterializer`'s resolution —
  is unaffected, exercised against the materializer's real read set (requirements §11).

## 12. Traceability

| Item | Coverage |
|---|---|
| REQ-001 | §2, §4, §5.2 |
| REQ-002 | §2, §5.2 |
| REQ-003 | §2, §6 |
| REQ-004 | §8 |
| REQ-005 | §5.3 |
| REQ-006 | §7.2 |
| REQ-007 / 008 / 009 | §8, §8.1 |
| REQ-010 | §7.3 |
| REQ-011 | §3.2, §8 |
| NFR-1 | §3.1 (`source_name`) |
| NFR-2 | §5 (admin group) |
| NFR-3 | §3.1 (no new posture) |
| NFR-4 | §8 (const deletion; arbitrary unprojected) |
| NFR-6 | §5.4 |
| NFR-7 | §6 (no new dependency) |
| NFR-8 | §4 |

## 13. Critic history

### Cycle 1 — verdict: needs_revision (independent, read-only critic)
Four blocking findings, all valid and now resolved:
- **B1** — migration numbered `0081` collided with existing `0081`/`0082` on disk (my earlier
  `ls | head -80` truncated at `0080`). Fixed → `0083`, with an implementer re-check note (§8, §8.1).
- **B2** — `SystemFriendlyName` misclassified as "not read"; it maps to `proj_systems.friendly_name`
  (migration `0082`), read by `GetPromotionGapRows.sql:43` and promoted at `DiscoveryMaterializer.cs:110`.
  Added to Tier 2; §4.2 corrected (§4.1, §4.2).
- **B3** — the fitness test only checked subset (no false positives), structurally unable to catch a
  missing exclusion (the B2 class). Redesigned to an exact set-equality completeness check against a
  co-located `IdentityInputColumns` manifest (§4.3).
- **B4** — `CustomFieldValue` deletion left dangling refs not in the file map. Enumerated the full
  grep'd reference set across `src` + `test` (§9).

Suggestions folded: **S1** (first segment must be a `Device[]` list segment — §5.2), **S2** (reject
Derived/Metric paths outright — §5.2), **S3** (metadata "path alone" = device-independent identity,
per-instance for child scope — §3.2), **S4** (per-device query served by id-prefix range on the
existing index, no new index — §7.1). S5 was the already-agreed REQ-011 correction (no action).

### Cycle 2 — verdict: *(pending re-run of the independent critic)*
