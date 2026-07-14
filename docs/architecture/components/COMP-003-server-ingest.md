---
id: COMP-003
name: Server Ingest
status: draft
---
## Responsibility

Server-side ingest path (`Server` class library, `FactIngestPipeline` + a new ingest controller in `Server.Web`). Iteration-2 contract change per ADR-001 (D2):

Receives `POST /api/v1/agent/facts` with body `{agent_id, fingerprints[], fact_batches[{fingerprints[], facts[]}]}` (gzip JSON). For each `fact_batch`:

1. **Authenticate** the agent (API key → `agent_id`); reject if the body `agent_id` mismatches the key or the agent is not `approved` (mirrors Go app 403 `not_approved`).
2. **Resolve device identity** by handing the batch fingerprints to Server.DeviceRegistry (COMP-004), which returns a stable `device_id` (creating a device if none matches, auto-merging on overlap per ADR-002).
3. **Rewrite fact IDs**: the agent emits facts against a placeholder root; ingest rewrites each fact's `id` and `key_values` to embed the resolved `Device[{uuid}]` key, producing canonical fact IDs (`Device[{uuid}].Path`). This is the step that lets the agent stay device-id-free.
4. **Append + project**: pass the resolved facts to the existing pipeline — `FactRepository.AppendAsync` (history dedup) + `ProjectionRouter.RouteAsync` (current-state). Analysis (COMP-005) runs as part of the agent-side analysis already; server-side derivations that require cross-device context run here.

Enforces `MaxFactsPerBatch` (50 000) per batch. Chunks DB writes at ~10K facts/round-trip (existing behavior).

## Interfaces

- **Inbound HTTP**: `POST /api/v1/agent/facts`, `POST /api/v1/agent/heartbeat` (see API-001). Validated, gzip-decoded, deserialized to `FactBatch` records.
- **→ COMP-004 Device Registry (in-process method call)**: `ResolveDeviceId(fingerprints) → (device_id, isNew, mergedFrom[])`.
- **→ COMP-006 Projections (in-process)**: `ProjectionRouter.RouteAsync(facts)`.
- **→ COMP-009 Database**: `FactRepository.AppendAsync` writes `facts_history`.

## Dependencies

- COMP-004 Server Device Registry
- COMP-005 Server Analysis
- COMP-006 Server Projections
- COMP-009 Database

## Integration Test Boundaries

- **Ingest → registry → history (DB)**: submit a batch with fingerprints for a new device; assert a `devices` row is created, fingerprints stored, and fact IDs in `facts_history` carry the resolved UUID.
- **ID rewrite correctness (DB)**: submit facts with placeholder roots; assert stored `id` and `key_values` embed `Device[{uuid}]` and `attribute_path` is unchanged (structural form is identity-independent).
- **Batch size guard (API)**: submit >50 000 facts; assert `400` with a domain error, no partial write.
- **Affinity note**: single server instance (ADR, D9/scale) makes EntityStateCache coherent by definition; no load-balancer hashing needed at this scale.
