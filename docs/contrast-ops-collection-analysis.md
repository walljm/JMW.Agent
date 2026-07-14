# Fact-Based Collection & Analysis: `ITPIE` (ops) vs `JMW.Agent`

A comparison of two fact-based network data-collection systems built by the same author:

- **ITPIE (ops)** — `vae/operations/source/ITPIE.*`. The mature network-device analysis platform. Collection starts in `ITPIE.Collector`; analysis in `ITPIE.DeviceAnalysis`; storage/identity in `ITPIE.Server`.
- **JMW.Agent** — `walljm/JMW.Agent`. The newer discovery/inventory system. `src/Core` (model), `src/Agent` (collectors), `src/Server.Web` (ingest + Postgres).

Both decompose the world into **facts** — one atomic, typed observation about one attribute of one entity, addressed by a hierarchical dotted path. They share obvious lineage; JMW.Agent reads as a deliberate second draft of the ITPIE fact model. But they diverge on where parsing lives, how identity is modeled, how data moves, how it's stored, and how analysis is structured. Findings below are verified against source.

---

## 1. The two pipelines at a glance

### ITPIE (ops)

ITPIE has **two collection modes** that converge on the same `Fact`. Only the SSH CLI path runs raw text through a parser framework; every other collector parses its structured source straight into facts (like JMW.Agent does).

```
--- SSH CLI path (the only one using the XML parser framework) ---
SSH device → SshOutputProvider [Connect → Identify(VOMV) → Fingerprint → Collect]
  → raw text → Parser.ProcessParse (declarative XML section parsers)
  → RelatedEntity{Table, string dict} → ParseResultContent → IParsedRow
  → NetworkNode.AnalyzeParsedResults (~40 hand-written, VOMV-gated analyzers)

--- every other collector (API / SNMP / BACnet / Modbus / vendor REST) ---
structured source → collector parses directly → emits Fact via *.Facts.cs

--- both modes then share ---
  → AnalyzedNodeResult{Context, IEnumerable<Fact>}
  → collection container (zip/dir): RAW OUTPUT + FACTS JSON, optionally encrypted
  → PushManager: multipart file upload to server
  → FingerprintState → server device-id resolution → dm.device.id
  → FactReceiver<TRow> reassembles facts → typed rows
  → binary COPY into ans_temp.* staging → ~200 ordered SQL merges → ans.* normalized tables
```

### JMW.Agent

```
host syscalls / SNMP / SSH / BACnet / Modbus / ~25 LAN scanners
  → collector parses directly → Fact.Create(FactPaths.*, keys, value)
  → AnalysisEngine: Normalize (per-path) + Derive (topo-sorted derivations)   [agent-side]
  → CollectorDeltaTracker: keep only CHANGED facts (per-id value hash)
  → gzip AgentFactsRequest → POST /api/v1/agent/facts
  → Fingerprint set → DeviceRegistry → stable DeviceId (auto-merge)
  → rewrite placeholder root to Device[{id}]
  → FactIngestPipeline (parallel):
       ├─ FactRepository → append-only facts_history  (LATERAL dedup)
       └─ ProjectionRouter → GenericProjection → proj_* current-state tables
  → DiscoveryMaterializer bootstraps passively-seen devices
```

---

## 2. Shared DNA

| Choice | ITPIE | JMW.Agent |
|---|---|---|
| Atomic fact = 1 attribute of 1 entity | ✔ | ✔ |
| Hierarchical dotted path with `[]` list markers | `NetworkDevices[].LogicalSystems[].ArpTable[].InterfaceName` | `Device[].Interface[].Speed` |
| Central path vocabulary as typed consts | `IdentifierCatalog` (~4,600 lines) + `KeyCatalog` | `FactPaths` / `ServicePaths` |
| No-boxing struct union for values | `FactValue` (16-byte, packed) | `FactValue` (32-byte, explicit layout) |
| Custom `System.Text.Json` union converters | ✔ | ✔ |
| Direct-to-fact emission from structured sources | ✔ (all non-SSH collectors) | ✔ (all collectors) |
| Device identity = set of normalized (type,value) fingerprints → stable id | `FingerprintState`/`IdentityState` → `dm.device.id` | `Fingerprint(Type,Value)` → `DeviceId` |
| SNMP engine id treated as the weakest identifier | ✔ (fallback-only / dropped if not alone) | ✔ |
| No ORM; raw/typed SQL over Npgsql, Postgres | ✔ | ✔ (source-generated `[DatabaseCommand]`) |

The overlap is real and deep. JMW.Agent kept the "one atomic attribute = one fact on a hierarchical path" philosophy, the no-box value union, and the fingerprint identity model, then simplified identity representation and modernized storage and analysis.

---

## 3. Where parsing lives

Direct-to-fact emission from structured sources is **common ground**. The one difference is that ITPIE *adds* a declarative parser framework for a modality JMW.Agent doesn't have: free-form CLI text.

- **ITPIE** — most collectors (`ActiveDirectory`, `Meraki`, `Bacnet`, `Modbus`, `Vmware`, `Wmi`, REST/API…) parse structured sources straight into facts. Only the **SSH CLI path** runs raw vendor text through the XML parser framework: commands and parsers ship in a `device-support.zip` catalog (`.dsc/.vsc/.msc`), `Parser.ProcessParse` runs regex section parsers into `RelatedEntity` tables → `IParsedRow` → analyzers. That framework has a GUI editor and AI-assisted parser generation. (`SNMP`/`GenericRestApi` are mixed.)
- **JMW.Agent** — every collector calls `Fact.Create(...)` directly. No parser framework, no intermediate DTO.

**Tradeoff.** The framework earns its complexity only because ITPIE must parse free-form CLI across thousands of vendor/OS/version combinations, ideally editable by non-developers. JMW.Agent has no free-form-text modality, so it needs none. If JMW.Agent ever adds CLI scraping, ITPIE's data-driven catalog is the pattern to reach for rather than hand-rolling per-device regex.

---

## 4. Fact identity model

Both address a fact by a hierarchical path, but represent the per-level identity differently.

- **ITPIE** — `Fact` = `Identifier` + ordered `IReadOnlyList<FactSelector>` + `FactValue`. Each `[]` gets one `FactSelector`, a dict of typed key/value pairs. **Compound keys are first-class**: an ARP entry is keyed by `InterfaceName` + `IpAddress` + `MacAddress` at once, each a typed `FactValue`.
- **JMW.Agent** — identity is embedded in the id string (`Device[router-1].Interface[eth0].Speed`) and decomposed once in `Fact.Create` into four stored fields: `AttributePath` (`Device[].Interface[].Speed`), `KeyValuesJson` (`{"Device":"router-1","Interface":"eth0"}`), `DimKey` (`Device|Interface`), `Attribute` (`Speed`). Downstream stages read fields, never re-parse.

**Tradeoff.** ITPIE's selector model is richer (compound + typed keys), which is what enables its stream-reassembly (§7). JMW.Agent's embedded identity is simpler and more operable: human-readable ids, 80–90% wire compression from shared prefixes, and precomputed columns that make SQL trivial (`WHERE attribute_path = … AND key_values->>'Interface' = …`) with no reconstruction. Its limit: one key per segment, stringly-typed. If it ever needs a two-attribute key it would be reinventing ITPIE's compound selector.

---

## 5. Device identity resolution

Both resolve "which physical device is this?" the same way: collect a **set of normalized (type, value) fingerprints**, map that set to a **stable integer device id** server-side, coalescing many fingerprints onto one device.

| | ITPIE | JMW.Agent |
|---|---|---|
| Identity primitive | `FingerprintState{UniqueId, UniqueIdType}` | `Fingerprint(Type, Value)` |
| Identifier types | MAC, SerialNumber (chassis), UUID, MachineId, SnmpEngineId, HostnameAndIp | mac, chassis-serial, disk-serial, uuid, machine-id, snmp-engine-id, ssh-host-key, bacnet/modbus ids |
| Per-type normalization before match | ✔ | ✔ |
| Multiple fingerprints per device | ✔ | ✔ |
| Weakest identifier demoted | ✔ SNMP engine id | ✔ SNMP engine id |
| Stable id anchor + mapping + history | `dm.device.id`, `dm.fingerprint`, `dm.fingerprint_history` | `DeviceId`, fingerprint rows, `device_aliases`, audit |
| Server resolution shape | 0 → create; 1 → attach; 2+ → detected | 0 → create; 1 → attach; 2+ → auto-merge |

The convergence goes right down to both independently treating the SNMP engine id as the weakest signal. The differences that remain:

1. **Representation — opaque uniform hash vs. structured, selectively-scoped pair.** *Both vendor-scope serials* (so `cisco:123` ≠ `juniper:123`) — this is shared intent, not a difference. The difference is uniformity: ITPIE folds vendor into *every* fingerprint via `SHA512(UPPER(Vendor)+NormalizedUniqueId)`, even MACs, storing the opaque hash under `UNIQUE(fingerprint)`. JMW.Agent keeps a readable `(Type, Value)` and vendor-scopes *selectively*: `chassis-serial`/`disk-serial`/BACnet/Modbus normalize to `{vendor}:{value}` (and `NormalizeSerial` rejects a serial with no vendor), while globally-unique identifiers (MAC, UUID, machine-id, engine id, host key) carry no vendor prefix. JMW.Agent's rule — scope only where the identifier isn't globally unique — is the more principled one; ITPIE's uniform hash is simpler but opaque (you can't see *why* two devices matched, and re-normalizing vendor names churns every fingerprint).

2. **Fingerprint source — parsed facts vs. gathered directly.** ITPIE *extracts* fingerprints from parsed command output (MACs from interface/OS tables, chassis serial from inventory) and **gates on a completed VOMV** — it can't fingerprint a device it hasn't identified to vendor/OS/model/version. JMW.Agent *gathers* fingerprints directly (`LocalMachineFingerprints`, protocol probes), **independent of analysis**, so it can identify a device a scanner saw only shallowly. This is the one place the domains genuinely diverge, and it tracks discovery vs. deep-analysis.

3. **Merge: implemented vs. pending.** JMW.Agent auto-merges the 2+ case (oldest survives, losers aliased into `device_aliases`, fingerprints reassigned, audit-logged) plus a manual path. ITPIE *detects* the 2+ case but currently leaves a `//TODO` (`ImportPipelineExtensions.cs:92-95`) — no auto-merge/alias yet. On this step the newer system is ahead of the mature one.

4. **Collection-time duplicate guard.** ITPIE adds `OpsDuplicateDeviceDetector`: if the same device (by fingerprint) is already being collected under a lower host IP, this run aborts — preventing concurrent double-collection of a device reachable at several IPs. JMW.Agent handles overlap purely at ingest.

---

## 6. How data moves

- **ITPIE** — full per-device snapshots as files (**raw output + facts**, optionally encrypted), uploaded via multipart and re-ingested wholesale. Replay providers (`LogFile`/`Offline`) can re-run parse+analyze from captured raw output.
- **JMW.Agent** — deltas streamed continuously: `CollectorDeltaTracker` ships only facts whose value changed since the last cycle, gzip-compressed, with `Rollback()` on failure.

**Tradeoff.** ITPIE's file model gives an auditable, replayable artifact — invaluable for debugging parser regressions and for regression baselines, plus air-gapped/manual collection and encryption at rest — at the cost of bandwidth, full re-ingest, and no continuous freshness. JMW.Agent's delta model gives low bandwidth and near-real-time freshness for a live fleet, but retains no raw artifact (you can't replay what a collector saw) and depends on the delta-state/rollback logic being exactly right.

---

## 7. Storage: normalized relational tables vs. materialized projections

This is the sharpest architectural divergence.

### ITPIE — hand-modeled normalized schema, reconstruct-then-store

Facts are transient. `FactReceiver<TRow>` reassembles the flat fact stream into typed rows (the `FactAssociator` reconciles *sub-entities within a device* here — e.g. an interface seen by `Name` in one fact and by `Index` in another — via `ReplaceAssociation`). The concrete `GenericDictionaryFactReceiver` is a column-mapping shim driven by a `DatabaseInsertionConfiguration(TableName, KeyMappings, ColumnMappings)`. Rows are then **binary-`COPY`'d into UNLOGGED per-aggregation staging partitions (`ans_temp.*`), and a hand-ordered chain of ~200 set-based SQL merge functions** folds them into the real tables.

The schema (`ans`) is **~140 hand-authored normalized tables**, wide typed columns (`MACADDR`, `INET`, `TSTZRANGE`), real FK cascades, and an explicit parent→child spine:

```
network_node → node_logical_sys_entry → vrf_entry → node_interface → {interface_ip, vlan, fhrp, stp, poe, dot1x, aggregate}
```

plus large families for routing, L1/L2/L3 topology relations, firewall, and UC/voice. Re-collection uses **three write strategies**: device/interface rows **upsert in place** (`ON CONFLICT … DO UPDATE`, `seen_range` extended); ARP/MAC/routes are **temporally versioned** (`TSTZRANGE seen_range`, GiST exclusion constraint, ranges closed out when no longer seen, `is_active` + last-known-good windowing); staging partitions are created and dropped per run. Roll-ups (port counts, `interface_aggregate`, interface rates from octet deltas) are **materialized into columns at merge time**.

### JMW.Agent — event-sourced facts + declarative projections

Two tiers, and facts are the source of truth:

- **`facts_history`** — append-only truth. Columns: `id`, `attribute_path`, `key_values` (JSONB), `kind` + typed value columns, `collected_at`. It is effectively schemaless for *new fact types* — a new attribute needs no migration. `FactRepository.AppendAsync` batches facts through `unnest` arrays and dedups via a **`LATERAL LIMIT 1` covering-index lookup** against the latest row per id, inserting only when the value actually changed.
- **`proj_*`** — 43 current-state tables, each generated from a declarative `ProjectionDef` (table + dimension names + `column→attribute→pg-type` map). `GenericProjection` builds its SQL once. Writes are guarded in two stages: an in-memory **`EntityStateCache`** (full per-entity state, default 500K entities ≈ 115 MB, warm-loaded from the projection table at startup) drops entities whose values haven't changed *before touching Postgres*; then a single batched `INSERT … unnest … ON CONFLICT (dims) DO UPDATE SET col = COALESCE(EXCLUDED, existing) … WHERE (EXCLUDED IS NOT NULL AND existing IS DISTINCT FROM EXCLUDED)`. `COALESCE` lets partial-column batches preserve prior values; the `IS DISTINCT FROM` guard is the fallback for cache-overflow entities. `ProjectionRouter` routes each fact O(1) by `(DimKey, Attribute)`. Projections are **disposable** — rebuildable from history.

### Tradeoffs & scaling

| Dimension | ITPIE (normalized tables) | JMW.Agent (history + projections) |
|---|---|---|
| Source of truth | The `ans.*` tables (facts discarded after reassembly) | `facts_history`; projections are derived/disposable |
| Add an attribute | Migration + `FactReceiver` mapping + analyzer | Add a column to a `ProjectionDef`; history needs no migration |
| Reshape a view | Re-ingest collection files | Rebuild a projection from history — no recollection |
| Time-travel / history | Only for temporal tables (ARP/MAC/routes) | Uniform — everything is in `facts_history` |
| Query model | Rich relational: FKs, joins, constraints, typed columns | Flat per-entity current-state tables keyed by dimension strings; weaker relational integrity |
| Write path | Binary COPY + ~200 ordered SQL merges | Cache-filtered batched `unnest` upserts (most unchanged entities never touch PG) |
| Write ceiling | Partition create/drop takes `ACCESS EXCLUSIVE` locks → serializes concurrent collections; ~40 indexes on hot tables slow writes | `EntityStateCache` is memory-bounded per projection; high-cardinality projections (per-interface at 80K devices) can overflow to the SQL guard |
| Storage growth | ARP/MAC/routes grow monotonically (temporal); `is_active` windowing keeps "current" queries fast | `facts_history` grows monotonically; projections stay bounded (one row per entity) |

The essential contrast: **ITPIE's schema *is* the domain model** — you get referential integrity, typed columns, and rich relational querying (topology joins, L1/L2/L3 relations) out of the box, but every new entity/attribute is a migration and evolving the model means re-ingesting. **JMW.Agent separates unbounded history from bounded current-state**, so it gets time-travel, cheap schema evolution, and rebuildable views for free, but its projections are flat and dimension-keyed by strings (no FKs), so complex relational integrity and cross-entity joins are weaker. Notably both are event-sourced for ARP/MAC/routes — ITPIE applies temporal versioning *selectively* (device/interface upsert in place, losing history), while JMW.Agent applies the history/current-state split *uniformly*.

---

## 8. Analysis: normalization, derivation, deduplication

Both normalize raw values to typed ones, derive computed values, and deduplicate — but one wires it by hand per entity, the other runs a small declarative engine.

### Normalization

- **ITPIE** — inline in analyzers via `Normalize*` static helpers inside `TypedRow.From`: big lowercasing `switch` expressions that fold vendor spellings into enums (`NormalizeAdminStatus`), regex-parse "10G"/"100 Mbps" into bps, etc. A second canonicalization pass happens in merge SQL (`get_mac_oui`) and Postgres column types (`MACADDR`/`INET`).
- **JMW.Agent** — a registry: `INormalizer`s keyed by `AttributePath`, applied by `AnalysisEngine.Normalize` (dictionary lookup, rewrite or drop the value). 5 normalizers today (MAC, interface-name, speed, disk-type, SMART-health).

### Derivation

- **ITPIE** — inline per-analyzer methods (`*.Derive.*.cs` partial files), rich `(vendor, os, value)` `switch` expressions: hardware-type classification, is-virtual across ~40 cases, interface-rate-from-octet-deltas, plus DB-side derivations in merge SQL. **No engine and no topological sort** — ordering is a *manual invariant* held in two places: statement order inside each analyzer, and the ~200-line hand-ordered call list in `MergeDeviceDataAsync`. Adding a derivation that depends on another means finding the right insertion point by hand.
- **JMW.Agent** — declarative `IDerivation` (declared `Inputs`/`Outputs`/optional `Scope` attribute-path patterns). `AnalysisEngine` **topologically sorts** derivations (Kahn's algorithm, with cycle detection), groups input facts by an inferred **scope** (intersection of list dimensions), runs each derivation per scope group, and feeds outputs back into the index so later derivations see them. Derived facts share the fact namespace — "the fact id is the identity," no derived flag. 4 derivations today. Runs **agent-side, per batch**.

**Tradeoff.** ITPIE's inline approach carries far more, and far deeper, derivation logic (it's a deep analysis system across ~90 entity types) with full control and no abstraction to fight — at the cost of hand-maintained ordering, no cycle detection, and poor engineering scalability (every new vendor/attribute edits a `switch` and finds a slot in the merge sequence). JMW.Agent's engine makes ordering automatic and safe and lets you add a derivation without touching collectors — but the scope-inference is implicit machinery, and with only 4 derivations the engine is lightly loaded today. It's the better bet *if* JMW.Agent's derivation set grows toward ITPIE-like depth; right now it's cheap insurance more than a load-bearing win.

### Deduplication — the intent differs, not just the mechanism

This is the subtler and more important contrast.

- **ITPIE dedup = MERGE-to-canonical.** Layered, all reconciling *multiple observations of the same entity into one best row*: `FactReceiver.CombineRows` (association-merge, prefer non-null) → per-analyzer `Dictionary<key, TypedRow>` merge on a **natural key** with hand-written collision policy (e.g. `NodeInventory` keyed by `SerialNumber` prefers the higher hardware-type rank, then null-coalesces every other field) → SQL `row_hash` de-dup in staging → final `ON CONFLICT` upsert. Domain rules decide the winner when sources disagree.
- **JMW.Agent dedup = CHANGE-DETECTION.** Layered, all answering *"did this fact's value change?"* to avoid redundant writes: agent `CollectorDeltaTracker` (primary — per fact id, value hash) → server history `LATERAL` diff (per id) → projection `EntityStateCache` + SQL guard (per entity). There is **no merge-to-canonical step**: because the fact id embeds device + attribute, two sources reporting the same attribute produce the *same id*, so conflicts resolve by **last-writer-wins** (`GREATEST(updated_at)`, `COALESCE` fills nulls only).

**Tradeoff.** When multiple sources/commands report the same entity with partial, conflicting data, ITPIE produces a cleaner canonical record — its per-entity merge rules (prefer the more-specific hardware type, coalesce fields) actively reconcile the disagreement. JMW.Agent's recency model is far simpler and needs no per-entity rules, and it fits its topology (one authoritative agent per host + shallow scanners; `DiscoveryMaterializer` does let agent-authoritative values win over passively-discovered ones via `COALESCE`). But at the per-fact-value level JMW.Agent has no authority ranking — if two collectors report conflicting values for the same attribute, the last one wins regardless of which source is more trustworthy. For genuinely conflicting multi-source data, ITPIE's explicit merge is more correct; for JMW.Agent's mostly-single-authoritative-source model, recency is usually enough.

---

## 9. Honest verdict

Neither is better in the abstract; each is tuned to its job.

- **ITPIE** — authoritative, deep analysis of network infrastructure, much of it scraped from vendor CLIs across enormous device diversity. Its heavyweight investments (the CLI parser catalog, VOMV gating, selector-rich facts, stream reassembly, a 140-table normalized schema with temporal versioning, ~90 hand-written analyzers with merge-to-canonical dedup) are justified by that diversity and depth. The price is a large bespoke system where new entities/attributes mean migrations and hand-ordered merge logic.
- **JMW.Agent** — broad, continuous discovery and inventory across a mixed fleet from mostly-structured sources. Its choices (direct fact emission, delta streaming, event-sourced history + declarative projections, a topo-sorted derivation engine, change-detection dedup) are justified by continuous multi-observer discovery and prioritize evolvability. The price is less analytical depth per device, no raw-output audit trail, and weaker relational integrity in the query model.

Defining choice of each: **ITPIE's is the normalized relational schema + reconstruct-then-store with per-entity merge-to-canonical**; **JMW.Agent's is event-sourced facts + rebuildable projections with change-detection dedup and a declarative analysis engine.** Genuine common ground, not differentiators: both emit facts directly from structured sources, and both resolve device identity through normalized typed fingerprints mapped to a stable id.

---

## 10. What each could borrow from the other

### JMW.Agent ← ITPIE
1. **Retain raw collector output (optional capture mode).** ITPIE's replay providers make collector bugs debuggable and enable golden-file testing. JMW.Agent keeps nothing of what a collector saw — the biggest gap.
2. **A regression-baseline harness.** ITPIE runs full parse+analyze against captured per-device `TestCaseData`. JMW.Agent's collectors deserve the same golden-file net.
3. **Merge-to-canonical where sources genuinely conflict.** If JMW.Agent grows authoritative multi-source overlap (e.g. SNMP vs. scan reporting the same attribute), ITPIE's natural-key merge with source/precision ranking beats blind last-writer-wins.
4. **Compound/typed keys** — proven answer if an entity ever needs a two-attribute key.

### ITPIE ← JMW.Agent
1. **Event-sourced storage.** Append-only facts + rebuildable projections would give uniform history and let the model evolve without re-ingesting collection files.
2. **Precomputed query fields.** `attribute_path` + `key_values` JSONB make ad-hoc SQL trivial without object reconstruction — cheap even on top of the existing model.
3. **A declarative normalize/derive engine.** Factoring the `Normalize*`/`Derive*` switches and the hand-ordered merge sequence into a registry with a dependency DAG would cut coupling and remove the manual-ordering invariant.
4. **Finish the device-merge case.** ITPIE detects but doesn't merge straddling fingerprints (`ImportPipelineExtensions.cs:92-95`); JMW.Agent's implemented auto-merge (oldest survives, aliased, reassigned, audited) is a ready blueprint.

---

## 11. Key file references

**ITPIE**
- Fact model: `ITPIE.DeviceAnalysis.Abstractions/Fact.cs:11`, `FactSelector.cs:18`, `FactValue.cs:37`; vocabulary `IdentifierCatalog.cs`
- Collection: `ITPIE.Collectors.Ssh/SshOutputProvider.cs:62` (identify `:287`; fingerprint `:358`; dup check `:165`; parse `:723,827`; analyze `:511`); parser framework `ITPIE.DeviceSupport.Parsing/Parser.cs:56`
- Analysis: `ITPIE.DeviceAnalysis/Models/NetworkNode.cs:16`; analyzers `Models/**/*.{Normalize,Derive}*.cs`; dedup idiom `Models/NodeInventory/NodeInventory.cs:96-132`
- Device identity: `Fingerprinting/FingerprintState.cs` (hash `:104`, types `:131`), `IdentityState.cs:111`; server resolution `ImportPipeline/ImportPipelineExtensions.cs:59` (merge TODO `:92`); id assignment `Repositories/FingerprintRepository.cs`
- Storage: reassembly `FactReceiver.cs:6,39` + `GenericDictionaryFactReceiver.cs`; write `DeviceAnalysis/DeviceAnalysisService.cs` (COPY `:319`); merges `AnalysisDatabaseMerge.cs` + `*.Sql.*.cs`; schema `Migrations/Scripts/Bootstrap/20251023000014-ans-ddl.sql` (~140 tables); staging `AnsTempPartitionRow.cs`
- Transport: `PushManager.cs:77`; artifact `ShellDeviceReaderWriter.cs:45,117`

**JMW.Agent**
- Fact model: `src/Core/Fact.cs:22` (derived fields `:37-91`), `FactValue.cs:39`, `FactSegment.cs:8,44`; vocabulary `src/Core/Analysis/FactPaths.cs`
- Analysis: `src/Core/Analysis/AnalysisEngine.cs` (normalize `:45`, derive `:108`, scope infer `:147`, topo-sort `:287`); `IDerivation.cs`; `AnalysisLibrary.cs` (5 normalizers, 4 derivations)
- Device identity: `src/Core/Fingerprint.cs`; `src/Core/FingerprintNormalizer.cs` (`NormalizeSerial:94`); `src/Server.Web/Ingest/DeviceRegistry.cs:49` (auto-merge `:245`)
- Transport/delta: `src/Agent/Collector/CollectorDeltaTracker.cs`; `src/Core/FactBatch.cs:25,56`
- Storage: `src/Server.Web/Ingest/FactRepository.cs:35` (LATERAL dedup `:89`); `Projections/ProjectionRouter.cs:34`, `GenericProjection.cs` (SQL gen `:280`, EntityStateCache `:347`, warm-up `:82`); `ProjectionLibrary.cs` (43 projections)
