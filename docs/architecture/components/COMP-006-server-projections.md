---
id: COMP-006
name: Server Projections
status: draft
---
## Responsibility

Current-state read model (`Server.Projections`, `GenericProjection` + `ProjectionRouter` + `ProjectionLibrary`). Maintains one row per entity in `proj_*` tables; these ARE the current-state view (no separate current-state facts table). Unchanged in structure from `scratch/`; the projection tables are the authoritative data source for all reporting views (DEC-005).

- Facts are routed to matching `GenericProjection` instances; projection rows are written only when tracked state changed. Two guards: EntityStateCache (application) + SQL `WHERE` guard (`IS DISTINCT FROM`).
- **EntityStateCache coherence**: at this deployment's scale (single host, a few hundred devices — constraints #9), a single server instance makes the per-instance cache coherent by definition. The load-balancer device-affinity mechanism in `scratch/architecture.md` is an 80K-device concern and is deferred (see ADR on inherited-scale mechanisms).
- **Route tables excluded** from the standard projection pipeline (DEC-004, REQ route note): high-cardinality (100K+/device) route entries are stored only in `facts_history` and queried directly; only summary facts (route count, default route) are projected.
- Reporting views (REQ-010..022, 037) read these tables; the change feed (REQ-022) reads `facts_history`.

## Interfaces

- **← COMP-003 Ingest (in-process)**: `ProjectionRouter.RouteAsync(facts)`.
- **→ COMP-009 Database**: upserts into `proj_*` tables with the WHERE guard.
- **← COMP-007 Web (in-process, read-only)**: report queries select from `proj_*` tables and `facts_history` (change feed) using keyset pagination.

## Dependencies

- COMP-009 Database
- COMP-003 Server Ingest

## Integration Test Boundaries

- **Write-guard suppression (DB)**: route an unchanged fact set twice; assert the second pass writes zero projection rows (WHERE guard blocks no-op upserts).
- **Route-table exclusion (DB)**: ingest a device with a large route table; assert no per-route projection rows are created and only summary facts are projected; raw routes are queryable from `facts_history`.
- **Read shape for reports (DB)**: assert each reporting view's query (hosts, services, ports, containers, storage, ARP) returns from a single projection table or a bounded join, with keyset pagination on an indexed sort column.
