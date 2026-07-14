---
id: ENTITY-001
name: Device
status: draft
---
## Fields
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| device_id | UUID (PK) | yes | Server-assigned stable identity. `gen_random_uuid()` default. |
| created_at | TIMESTAMPTZ | yes | First-seen timestamp. Default `now()`. |
| management_status | TEXT | yes | `managed` (collection target) or `discovered` (passive). New in iter-2 (REQ-030). Default `managed`. |
| merged_from | UUID[] | no | Alias device IDs auto-merged into this survivor (ADR-002, REQ-052/053). |
| updated_at | TIMESTAMPTZ | yes | Last mutation (status change, merge, fingerprint association). New in iter-2. Default `now()`. |

## Relationships
- 1:N → DeviceFingerprint (ENTITY-002) via `device_id`.
- 1:N → facts in `facts_history` (key `Device[{device_id}]` embedded in `key_values`).
- 1:N → Target (ENTITY-004) (a managed device may have targets).
- Self-referential via `device_aliases` (loser → survivor) for merge redirects.

## PII Fields
None directly. Associated facts (hostname, users, sessions) may be sensitive but are not on this row.

## Ownership
Owned by Server.DeviceRegistry (COMP-004). Created at ingest; mutated by merge/promote.

## Migration Strategy
**Brownfield** — `devices` already exists in `scratch/Schema.sql` (`device_id`, `created_at` only).
- **Transition**: rolling, additive (no downtime). All changes are `ADD COLUMN ... DEFAULT`.
- **Steps**: `ALTER TABLE devices ADD COLUMN management_status TEXT NOT NULL DEFAULT 'managed'`; `ADD COLUMN merged_from UUID[] NOT NULL DEFAULT '{}'`; `ADD COLUMN updated_at TIMESTAMPTZ NOT NULL DEFAULT now()`. Create `device_aliases` table.
- **Backfill**: existing rows default to `management_status='managed'` (pre-iter-2 devices were all collection-driven), `updated_at=created_at` via a one-time `UPDATE devices SET updated_at = created_at`.
- **Rollback**: `DROP COLUMN` for the three additions and `DROP TABLE device_aliases`; pre-iter-2 code ignores them, so rollback is safe.
- **Validation**: assert no NULLs in new NOT NULL columns; assert row count unchanged; assert a sample managed device still resolves by fingerprint.
