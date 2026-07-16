---
id: API-002
path: /api/v1/devices
method: GET
status: draft
---
## Description

Device management endpoints (user principals, session-cookie auth). Powers device list/detail and merge/promote operations. Reads from projection tables (DEC-005); writes go through Server.DeviceRegistry (COMP-004).

- `GET /api/v1/devices` — list (keyset paginated).
- `GET /api/v1/devices/{id}` — detail.
- `POST /api/v1/devices/{id}/merge` — manual merge (REQ-053, admin).
- `DELETE /api/v1/devices/{id}` — hard-delete (admin). Manual fallback for a bad merge; not a re-split (see ADR-002 Consequences) — deletes the device and everything attached to it (fingerprints, projections, history, incidents, change events) so the next observation of that physical entity resolves as a brand-new device.
- `POST /api/v1/devices/{id}/promote` — discovered → managed (REQ-036, admin).

## Request Contract
- `GET /api/v1/devices?after=<opaque>&limit=50&status=managed|discovered&q=<filter>` — filters optional.
- `GET /api/v1/devices/{id}` — path param `id` = device UUID (alias UUIDs resolve forward via `device_aliases`).
- `POST /api/v1/devices/{id}/merge` body `{ "into_device_id": "uuid" }` — merges `{id}` (loser) into survivor.
- `DELETE /api/v1/devices/{id}` — no body. If the same fingerprint overlap caused the bad merge, deleting alone won't stop it recurring on rediscovery — pair with excluding the offending fingerprint (`excluded_fingerprints`) when that's the case.
- `POST /api/v1/devices/{id}/promote` body `{ "agent_id": "uuid", "credential_id": "uuid", "target_address": "<ip-or-hostname>" }` — required. Promotion carries the agent, credential, and address needed to stand up active SSH/SNMP collection for the now-managed device (SCR-022); the server creates the backing `collection_target` as part of the promote transaction.

## Response Contract
- list → `200 { "items": [ { "device_id", "hostname", "vendor", "kind", "management_status", "last_seen", "fingerprint_count" } ], "next_cursor": "opaque|null" }` (the `next_cursor` value is passed back as `?after=`).
- detail → `200` device + projected child summaries (system, hardware, interfaces, disks, filesystems, security, updates) + fingerprints (`fp_type, fp_value, source, last_seen`) + `merged_from[]`.
- merge → `200 { "survivor_device_id": "uuid", "merged_from": ["uuid"] }`.
- delete → `200 { "deleted_device_id": "uuid" }`.
- promote → `200 { "device_id": "uuid", "management_status": "managed" }`.

## Pagination
**Keyset** on `(last_seen DESC, device_id)` with an index on that tuple. Opaque base64 cursor encodes the last `(last_seen, device_id)`. No offset/limit.

## Error Responses
Uniform envelope. `401 unauthorized`, `403 forbidden` (viewer attempting merge/promote — RBAC), `404 device_not_found`, `409 merge_conflict` (survivor==loser or already merged), `422 invalid_cursor`.

## Cross-References
REQ-037 (ex-REQ-011, superseded), REQ-012, REQ-030, REQ-036, REQ-052, REQ-053. COMP-004, COMP-006, COMP-007. ENTITY-001, ENTITY-002. ADR-002.
