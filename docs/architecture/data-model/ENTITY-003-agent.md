---
id: ENTITY-003
name: Agent
status: draft
---
## Fields
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| agent_id | UUID (PK) | yes | Agent-generated at first start (self-registration, D4). |
| hostname | TEXT | yes | Reported by the agent on register/heartbeat. |
| status | TEXT | yes | `pending` \| `approved` \| `disabled` (REQ-003). Default `pending`. |
| api_key_hash | TEXT | yes | Salted hash of the issued API key. Plaintext never stored. |
| last_heartbeat | TIMESTAMPTZ | no | Updated each heartbeat; drives online/offline status (REQ-004). |
| zone | TEXT | no | Logical grouping/zone (matches `AgentConfig.Zone`, constraints #9). |
| version | TEXT | no | Agent build version reported on register/heartbeat. |
| passive_discovery_mode | TEXT | no | `full` \| `degraded` — raw-socket capability (D5, REQ-038). |
| created_at | TIMESTAMPTZ | yes | Registration time. Default `now()`. |

## Relationships
- 1:N → Target (ENTITY-004) via `agent_id`.
- 1:N → facts (the agent that submitted them; `facts_history` is keyed by device, not agent — agent is provenance only).

## PII Fields
`hostname` may be considered low-sensitivity infrastructure metadata; no personal PII.

## Ownership
Owned by Server.Auth (COMP-008) for status/key; written by the register/heartbeat handlers.

## Migration Strategy
**New table** in iter-2 (no existing data). `CREATE TABLE IF NOT EXISTS agents (...)`. No backfill. Rollback: `DROP TABLE agents` (and dependent `targets` FK). Validation: register an agent end-to-end, assert row created with `status='pending'`.

Note: `clear_trackers_requested_at` (TIMESTAMPTZ, nullable) was added later to support an
admin-triggered agent delta-tracker cache clear — set by `POST /admin/agents/{id}/clear-cache`,
read back into `HeartbeatConfig.ClearTrackersRequestedAt` on the next heartbeat.
