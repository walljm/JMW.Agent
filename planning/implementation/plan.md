# Implementation Plan

Migrating from the current v1 (flat `devices` table, config-file terrain, JSON inventory blobs, fixed alert enums) to the v2 architecture (entity model, pipeline, Ingestor, DEK, expanded alerting).

## Guiding Principles

1. **Side-by-side.** Old schema/code stays in place until the new path is proven. Migration 0016 adds new tables; migration 0017+ backfills from old; migration 0018+ drops old tables.
2. **One stage at a time.** Each phase has its own validation checkpoint. No phase starts until the prior one passes `make test`.
3. **Agent is untouched through Phase 1–4.** The wire format doesn't change until Phase 5 (agent tick coalescing). Agent-side collection code is already good; the refactor is server-side.
4. **Migrations are reversible per-phase.** Each phase's migration can be rolled back (DROP IF EXISTS for new tables; no destructive changes to old tables until Phase 7).

---

## Phase 0 — Foundation (infra, DEK, config cleanup)

Goal: Server config matches architecture (no `[terrain]` in TOML; DEK exists; Sources table exists).

| # | File / Area | Work |
|---|-------------|------|
| 0.1 | `internal/server/config/config.go` | Remove `TerrainConfig` struct. Add `LogLevel`, rename `Addr`→`Listen`. Add retention defaults struct. |
| 0.2 | `internal/server/dek/dek.go` (new) | DEK generation, load-or-create, AES-256-GCM encrypt/decrypt helpers. |
| 0.3 | `internal/server/dek/dek_test.go` (new) | Round-trip encrypt/decrypt, key file permissions check, rotation helper. |
| 0.4 | `migrations/0016_entity_model_foundation.sql` | CREATE tables: `sources`, `notification_channels`, `maintenance_windows`, `subsystems`, `agent_subsystems`. FK refs to existing `agents`. |
| 0.5 | `internal/server/store/sources.go` (new) | CRUD for Sources table. Encrypt/decrypt `config_json` secrets via DEK. |
| 0.6 | `internal/server/store/sources_test.go` (new) | Insert, read-back (verify masked), update secrets, list enabled. |
| 0.7 | `cmd/server/main.go` | Wire DEK init at startup (load or create `$data_dir/server.key`). Legacy `[terrain]` migration notice + one-time import into `sources` row. |
| 0.8 | Smoke test | Server starts, legacy terrain config imported, new sources table queried. |

**Checkpoint:** `make test` passes. Server boots with old and new config. Terrain still polls using the new Sources-based config.

---

## Phase 1 — Entity Model (new tables, no writers yet)

Goal: Full entity schema exists in SQLite. No code reads/writes it yet (except tests).

| # | File / Area | Work |
|---|-------------|------|
| 1.1 | `migrations/0017_entity_model_core.sql` | CREATE: `hardware`, `systems`, `interfaces`, `interface_addresses`, `interface_networks`, `observations`, `hostname_aliases`, `services`, `disks`, `disk_smart_attributes`, `disk_partitions`. All with proper FKs and indexes. |
| 1.2 | `migrations/0018_entity_model_extensions.sql` | CREATE: `interface_profile`, `interface_mdns_services`, `interface_mdns_txt`, `interface_tls_sans`, host-posture tables (update_status, security_posture, etc.), container detail tables, hardware extension tables. |
| 1.3 | `internal/server/store/entities.go` (new) | Go structs for Hardware, System, Interface, InterfaceAddress, Observation, Service, Disk. Basic CRUD scaffolding (insert, get-by-id, upsert). |
| 1.4 | `internal/server/store/entities_test.go` (new) | Insert a Hardware → System → Interface → Address chain. Verify FKs. |
| 1.5 | `internal/server/store/posture.go` (new) | Writer methods for host-posture extension tables (bulk-replace per agent). |
| 1.6 | `internal/server/store/posture_test.go` (new) | Round-trip for each posture table. |

**Checkpoint:** `make test` passes. Schema is correct. Old `devices` table untouched, still serves the running app.

---

## Phase 2 — Identity Resolver + Pipeline Core

Goal: The 5-stage pipeline exists and can process a synthetic observation end-to-end in tests.

| # | File / Area | Work |
|---|-------------|------|
| 2.1 | `internal/server/pipeline/resolver.go` (new) | `IdentityResolver` — resolves MAC → Interface, (agent_id, device_name) → Disk, full observation → (Hardware, System, Interface). Fresh instance per batch. |
| 2.2 | `internal/server/pipeline/resolver_test.go` | New MAC → creates Interface + Hardware; known MAC → returns existing; agent correlation. |
| 2.3 | `internal/server/pipeline/pipeline.go` (new) | Pipeline struct with `Process(ctx, []Observation) (Result, error)`. Stages 1–5 in order. Single-transaction commit. |
| 2.4 | `internal/server/pipeline/pipeline_test.go` | Feed synthetic observations; verify entity creation, bucketing, hostname priority, merge. |
| 2.5 | `internal/server/pipeline/derive.go` (new) | Derive stage: interface_profile write, hostname canonical selection, stale-entity marking. |
| 2.6 | `internal/server/pipeline/derive_test.go` | Priority-based hostname wins; stale after threshold. |

**Checkpoint:** `make test` passes. Pipeline processes test observations and writes correct entity state. No HTTP integration yet.

---

## Phase 3 — Adapters + Ingestor

Goal: Adapters translate existing wire formats (agent-discovery, agent-inventory, terrain-dhcp) into `[]Observation`. Ingestor wires adapters → pipeline.

| # | File / Area | Work |
|---|-------------|------|
| 3.1 | `internal/server/pipeline/adapter.go` (new) | `Adapter` interface definition. |
| 3.2 | `internal/server/pipeline/adapters/agent_discovery.go` (new) | Translates `proto.DiscoveryRequest` → `[]Observation`. |
| 3.3 | `internal/server/pipeline/adapters/agent_inventory.go` (new) | Translates `proto.Inventory` → observations + posture writes. |
| 3.4 | `internal/server/pipeline/adapters/terrain_dhcp.go` (new) | Translates terrain DHCP lease list → `[]Observation`. |
| 3.5 | `internal/server/pipeline/adapters/terrain_dns.go` (new) | Translates terrain DNS query log → `[]Observation`. |
| 3.6 | `internal/server/pipeline/ingestor.go` (new) | `Ingestor` struct: adapter registry, per-batch resolver construction, pipeline dispatch. `Ingest(ctx, kind, payload)` + `PollSource(ctx, src)`. |
| 3.7 | `internal/server/pipeline/ingestor_test.go` | End-to-end: feed a DiscoveryRequest → verify entities written. |
| 3.8 | `internal/server/pipeline/adapters/agent_discovery_test.go` | Adapter produces correct Observation fields from sample request. |
| 3.9 | `internal/server/pipeline/adapters/terrain_dhcp_test.go` | Same for terrain DHCP. |

**Checkpoint:** `make test` passes. All adapters have tests. Ingestor can process each known `kind`.

---

## Phase 4 — HTTP Integration (dual-write)

Goal: HTTP handlers call Ingestor. Old `devices` table is ALSO still written (dual-write) so the old UI keeps working.

| # | File / Area | Work |
|---|-------------|------|
| 4.1 | `internal/server/http/server.go` | Add `Ingestor` field. Construct at startup with all adapters. |
| 4.2 | `internal/server/http/agent_api.go` | In `handleDiscoveries`, call `Ingestor.Ingest(ctx, "agent-discovery", req)` alongside existing `UpsertDevice` calls. |
| 4.3 | `internal/server/http/agent_api.go` | In `handleInventory`, call `Ingestor.Ingest(ctx, "agent-inventory", req)` alongside existing `SetAgentInventory`. |
| 4.4 | `internal/server/http/terrain.go` | Refactor terrain handler to call `Ingestor.PollSource` instead of calling terrain poller + `UpsertDevice` directly. Old UpsertDevice still called in parallel (dual-write). |
| 4.5 | `internal/server/pipeline/scheduler.go` (new) | Polled-source scheduler goroutine: loads enabled sources, checks `due(src)`, calls `Ingestor.PollSource`. |
| 4.6 | Integration test | Start server, register agent, send discovery + inventory + terrain tick. Verify both old `devices` rows AND new entity rows created. |

**Checkpoint:** `make test` + manual run. Both old and new data paths produce data. Old UI still works.

---

## Phase 5 — Metrics Pipeline + Snapshots

Goal: Metric snapshots flow through the Ingestor. Old `InsertSnapshots` still called (dual-write).

| # | File / Area | Work |
|---|-------------|------|
| 5.1 | `internal/server/pipeline/adapters/agent_metrics.go` (new) | Translates `proto.MetricsRequest` → snapshot writes via store. (Metrics don't produce Observations; adapter writes directly to snapshot tables.) |
| 5.2 | `internal/server/store/metrics.go` | Add new snapshot tables (filesystem_snapshots, temperature_snapshots, battery_snapshots, processes_snapshot) alongside existing ones. Add rollup writers. |
| 5.3 | `migrations/0019_metrics_expansion.sql` | CREATE: `filesystem_snapshots`, `temperature_snapshots`, `battery_snapshots`, `processes_snapshot`. Add rollup tables (`metric_snapshots_5min`, `_hourly`, `_daily`). |
| 5.4 | `internal/server/store/rollup.go` (new) | Rollup logic: raw → 5min → hourly → daily. Background goroutine. |
| 5.5 | `internal/server/store/rollup_test.go` | Insert raw data, run rollup, verify aggregated rows. |
| 5.6 | Back-pressure | Add `503 Retry-After` when write queue depth exceeds threshold (wire into HTTP middleware). |

**Checkpoint:** `make test` passes. Rollups run. New snapshot tables populated alongside old.

---

## Phase 6 — Alerting v2

Goal: Alert evaluator uses `metric_kind` + `metric_path` dispatch, supports all target_kinds, anti-flap, maintenance windows.

| # | File / Area | Work |
|---|-------------|------|
| 6.1 | `migrations/0020_alerting_v2.sql` | ALTER `alert_rules`: drop `metric` column, add `metric_kind`, `metric_path`. Add `flapping` to `alert_firings`. CREATE `maintenance_windows`. Add `rate_limit_per_hour` to `notification_channels`. Backfill existing rules to `numeric_snapshot` / `snapshot.<old_metric>`. |
| 6.2 | `internal/server/alerting/dispatch.go` (new) | Evaluator dispatch table: one function per `metric_kind` that knows how to query. |
| 6.3 | `internal/server/alerting/dispatch_test.go` | Each metric_kind returns correct values from test fixtures. |
| 6.4 | `internal/server/alerting/evaluator.go` (rewrite) | Replace `evaluateOnce` with target_kind resolution (agent, tag, source, disk, etc.), maintenance-window check, anti-flap dampening. |
| 6.5 | `internal/server/alerting/evaluator_test.go` | Fire/resolve cycle, flap detection, maintenance window suppression, rate-limit bypass for critical. |
| 6.6 | `internal/server/store/alerts.go` | Update queries for new schema (metric_kind, metric_path, expanded target_kind). CRUD for maintenance_windows. |
| 6.7 | `internal/server/notify/notify.go` | Add per-channel rate-limit enforcement. Coalesced summary emission at window end. |

**Checkpoint:** `make test` passes. Old rules migrated. New rule types (posture, source_health) can fire. Anti-flap and maintenance windows work in tests.

---

## Phase 7 — New UI Views (entity-based)

Goal: Presentation layer reads from new entity tables. Device list/detail, Sources page, updated Alerts page.

| # | File / Area | Work |
|---|-------------|------|
| 7.1 | `internal/server/store/views.go` (new) | Read-only query methods: `ListHardware`, `GetHardwareDetail`, `ListSources`, `GetSourceDetail`, `ListMaintenanceWindows`. |
| 7.2 | `internal/server/http/devices.go` (rewrite) | New device list/detail reading from entity tables instead of flat `devices`. |
| 7.3 | `internal/server/http/sources.go` (new) | Sources CRUD page. Secret masking. Per-kind forms. |
| 7.4 | `internal/server/http/alerts.go` (update) | Updated rule editor with `metric_kind` picker, expanded `target_kind` dropdown, maintenance window management tab. |
| 7.5 | Templates | New/updated HTML templates for devices, sources, alerts. |
| 7.6 | `internal/server/http/api_sources.go` (new) | JSON API for Sources CRUD + `PATCH .../secrets` write-only endpoint. `PATCH .../channels/{id}/secrets`. |

**Checkpoint:** Manual testing. New views show entity data. Old views still work in parallel.

---

## Phase 8 — Cutover + Old Table Removal

Goal: Remove dual-write. Old `devices` table dropped. All reads come from entity model.

| # | File / Area | Work |
|---|-------------|------|
| 8.1 | `internal/server/http/agent_api.go` | Remove old `UpsertDevice` calls. Ingestor is the sole write path. |
| 8.2 | `internal/server/http/terrain.go` | Remove old terrain-specific handler. Scheduler is the sole poll path. |
| 8.3 | `internal/server/store/devices.go` | Delete file (or gut to a thin compat layer for data access during migration window). |
| 8.4 | `migrations/0021_drop_legacy.sql` | DROP TABLE `devices`, `device_sightings`, `device_hostnames`, `device_networks` (old junction). Drop `agents.inventory_json`, `agents.primary_ip`. |
| 8.5 | `internal/server/config/config.go` | Remove legacy `[terrain]` migration shim (the one-release grace period is over). |
| 8.6 | `internal/server/terrain/` | Delete package entirely (replaced by adapters + scheduler). |
| 8.7 | Full test suite | `make test`, `make vet`, manual smoke test against a real agent. |

**Checkpoint:** Clean build. No references to old `devices` table. Agent ↔ server round-trip verified.

---

## Phase 9 — Agent Wire Optimization (tick coalescing)

Goal: Agent sends coalesced `/tick` instead of separate `/metrics` + `/discoveries`.

| # | File / Area | Work |
|---|-------------|------|
| 9.1 | `internal/shared/proto/tick.go` (new) | `TickRequest` envelope with subsystem hash, metrics, discoveries, optional inventory delta. |
| 9.2 | `internal/agent/transport/transport.go` | Replace separate POST calls with single `/tick` POST. |
| 9.3 | `internal/server/http/agent_api.go` | New `POST /api/v1/agent/tick` handler → Ingestor dispatch for each section. Backwards-compat: old endpoints remain as aliases for one release. |
| 9.4 | Subsystem hash + `If-Subsystems-Stale` | Agent sends subsystem hash; server responds with stale flag if mismatch. Agent sends full subsystem list on next tick. |
| 9.5 | Agent ↔ server integration test | Full tick flow verified in smoke test. |

**Checkpoint:** `make test`. Old endpoints still work (backwards compat). New `/tick` is primary.

---

## Phase 10 — Cleanup + Hardening

| # | File / Area | Work |
|---|-------------|------|
| 10.1 | Remove old `/api/v1/agent/metrics`, `/discoveries` aliases (after one release cycle). |
| 10.2 | Add `store/views_test.go` for the complex JOIN queries. |
| 10.3 | Foundation Critic grep checks: no handler imports `adapters` package; `views.go` has no writes; posture tables only written from `posture.go`. |
| 10.4 | Performance smoke test: 30 synthetic agents, 30-second ticks, verify WAL writer queue stays < 50. |
| 10.5 | API versioning header (`X-API-Version: 1`) on all responses. Document breaking change policy. |
| 10.6 | DEK rotation CLI command (`jmw-server rotate-key`). |
| 10.7 | Webhook body template documentation + example. |

---

## Summary

| Phase | Description | Approx. files touched |
|-------|-------------|----------------------|
| 0 | Foundation (DEK, config, sources table) | ~8 |
| 1 | Entity model (schema, structs) | ~6 |
| 2 | Pipeline + Identity Resolver | ~6 |
| 3 | Adapters + Ingestor | ~10 |
| 4 | HTTP integration (dual-write) | ~6 |
| 5 | Metrics expansion + rollups | ~6 |
| 6 | Alerting v2 | ~8 |
| 7 | New UI views | ~8 |
| 8 | Cutover + drop legacy | ~8 |
| 9 | Agent tick coalescing | ~5 |
| 10 | Cleanup + hardening | ~7 |

Each phase is independently testable. Phases 0–6 can be done without touching the running UI. Phase 7 introduces new views alongside old. Phase 8 is the point of no return for the old schema.
