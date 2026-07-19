---
id: ENTITY-004
name: Target
status: draft
---
## Fields
Unified table (`targets`) — merges what were originally two parallel concepts,
device-polling collection targets and service-polling targets, into one row shape.
One collector type allowlist covers both device-style protocols (ssh, snmp, bacnet,
modbus, google-wifi) and service-style ones (technitium-dns, home-assistant); `http`
and `cert` remain allowlisted but have no dedicated collector today.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| target_id | UUID (PK) | yes | Identity. `gen_random_uuid()`. |
| agent_id | UUID (FK) | yes | Owning agent (which agent polls this target). |
| endpoint | TEXT | yes | Interpreted per `endpoint_kind`: for `address`, a bare host/IP for device-style collector types or a full URL for service-style ones (REQ-006); for `mac`, a canonical bare 12-hex-lowercase MAC. |
| endpoint_kind | TEXT | yes | `address` (default) or `mac`. A `mac` target is resolved to the device's current IP at config-assembly time (`GetIpForMac.sql`), so collection follows DHCP moves instead of pinning to a stale address. Not allowed for URL-style collectors. `CHECK` enforces the value set and that a `mac` endpoint is bare 12-hex. |
| collector_type | TEXT | yes | `ssh` \| `snmp` \| `http` \| `cert` \| `bacnet` \| `modbus` \| `google-wifi` \| `technitium-dns` \| `home-assistant` (which collector). |
| credential_id | UUID (FK) | no | Reference to Credential (ENTITY-005); NULL for credential-free collector types. |
| label | TEXT | no | Optional human-readable name shown in the Targets tab and agent logs. |
| enabled | BOOLEAN | yes | Per-target enable/disable (REQ-008). Default true. |
| created_at | TIMESTAMPTZ | yes | Default `now()`. |
| updated_at | TIMESTAMPTZ | yes | Default `now()`. |

## Relationships
- N:1 → Agent (ENTITY-003) via `agent_id`.
- N:1 → Credential (ENTITY-005) via `credential_id`.
- Surfaced to the agent in its server-side config payload (DEC-001) on heartbeat, as one
  `HeartbeatConfig.Targets` list (agent-side: `AgentConfig.Targets`, one merged `Target` record —
  see constraints #9). The agent always receives a concrete `Endpoint`: `mac` targets are resolved
  server-side in `AgentConfigAssembler` and are skipped for a cycle when the MAC has not yet been
  seen on the agent's LAN (nothing to resolve), so the agent contract is unchanged.

## PII Fields
None. Credentials are referenced, not embedded.

## Ownership
Owned by Server.Web admin (COMP-007) via `TargetsApi`; read by the agent via config pull.

## Migration Strategy
**Merged table.** Originally shipped as two separate tables, `collection_targets` (device-style,
address/protocol) and `service_targets` (service-style, url/service_type) — unified into one
`targets` table (`endpoint`/`collector_type`) by
`ALTER TABLE collection_targets RENAME TO targets` + column renames + `INSERT ... SELECT` from
`service_targets`, then `DROP TABLE service_targets`. Home Assistant rows were reconciled during
the same migration: a `home-assistant-devices` row's credential wins over a co-existing
`home-assistant` row (renamed to `home-assistant`), since only the device-registry target's
credential (a Core Long-Lived Access Token) ever actually worked. Rollback: restore from the
pre-migration `pg_dump` backup. Validation: create/edit a target via the admin API, assert it
appears in the agent's next config pull with the correct `collector_type`.
