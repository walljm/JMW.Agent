---
id: COMP-009
name: Database
status: draft
---
## Responsibility

Single PostgreSQL database for **all** server data (ADR-005, D9) — no separate SQLite. Holds:

- **Facts**: `facts_history` (append-only change log; the only fact table).
- **Projections**: `proj_*` current-state tables (read model for reporting).
- **Device registry**: `devices`, `device_fingerprints`, and new `device_aliases` (merge redirects).
- **Server configuration**: `agents`, `targets` (unified device+service polling targets), `credentials` (encrypted `bytea`).
- **Auth/audit**: `users`, `user_sessions`, `audit_log`.

Accessed via `NpgsqlDataSource` (registered through `NpgsqlDataSourceBuilder`, injected) — never raw `NpgsqlConnection` (DotNet guidance). Raw SQL via a typed query approach; no ORM (constraints #3).

Schema changes for iteration 2 are documented in `planning/architecture/schema-additions.md` and applied to `scratch/Schema.sql`. All list/report queries use **keyset (cursor-based) pagination** with indexes on the sort columns (constraints, DB guidance).

## Interfaces

- **`NpgsqlDataSource`** injected into all server services (COMP-003/004/006/007/008).
- DDL: `scratch/Schema.sql` (idempotent `CREATE TABLE IF NOT EXISTS`), extended per schema-additions.md.

## Dependencies

- None (leaf).

## Integration Test Boundaries

- **Connection lifecycle (DB)**: assert all access goes through the registered `NpgsqlDataSource`; no direct `NpgsqlConnection` construction (static check + runtime smoke).
- **Keyset pagination (DB)**: paginate a `proj_*` table by indexed cursor; assert `EXPLAIN` shows index use and no offset scan.
- **Schema idempotency (DB)**: apply `Schema.sql` twice; assert no errors (all `IF NOT EXISTS`).
- **Migration safety (DB)**: apply iteration-2 additive columns to a populated `devices`/`device_fingerprints`; assert existing rows get correct defaults (`management_status='managed'`, `updated_at=now()`, `last_seen=first_seen`) and no data loss.
