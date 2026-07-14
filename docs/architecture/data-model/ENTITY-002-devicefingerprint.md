---
id: ENTITY-002
name: DeviceFingerprint
status: draft
---
## Fields
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| fp_type | TEXT (PK part) | yes | Fingerprint type (`mac`, `chassis-serial`, `uuid`, `snmp-engine-id`, `ssh-host-key`, `machine-id`, ...). |
| fp_value | TEXT (PK part) | yes | Normalized value (always via `FingerprintNormalizer`). |
| device_id | UUID (FK) | yes | Owning device. `(fp_type, fp_value)` PK enforces one device per fingerprint. |
| first_seen | TIMESTAMPTZ | yes | When this fingerprint was first associated. Default `now()`. |
| last_seen | TIMESTAMPTZ | yes | Most recent observation. New in iter-2 (REQ-038 confidence/recency). Default `now()`. |
| source | TEXT | no | Which collector/agent reported this (REQ-038 discovery-source tracking). New in iter-2. e.g. `ssh`, `snmp`, `passive:mdns`. |

## Relationships
- N:1 → Device (ENTITY-001) via `device_id` (FK, `ON DELETE CASCADE` for merge).
- Index on `device_id` (existing) for reverse lookup.

## PII Fields
None. MAC/serial are device identifiers, not personal data.

## Ownership
Owned by Server.DeviceRegistry (COMP-004).

## Migration Strategy
**Brownfield** — table exists (`fp_type`, `fp_value`, `device_id`, `first_seen`).
- **Steps**: `ADD COLUMN last_seen TIMESTAMPTZ NOT NULL DEFAULT now()`; `ADD COLUMN source TEXT`.
- **Backfill**: `UPDATE device_fingerprints SET last_seen = first_seen` once; `source` left NULL for pre-iter-2 rows (unknown origin).
- **Rollback**: `DROP COLUMN last_seen, source`. Additive only — safe.
- **Validation**: assert `last_seen >= first_seen` for all rows; row count unchanged.
