---
id: API-003
path: /api/v1/admin
method: GET
status: draft
---
## Description

Admin endpoints (user principals, session-cookie auth, **admin role required** — RBAC REQ-002). Manage agents, targets (device- and service-style polling, unified), credentials, fingerprint-conflict resolution, and audit-log reads.

- `GET /api/v1/admin/agents`, `PATCH /api/v1/admin/agents/{id}/approve` (and `/disable`).
- `PATCH /api/v1/admin/agents/{id}` — update agent metadata (name/zone/enabled).
- `PUT /api/v1/admin/agents/{id}/config` — update collection intervals.
- `POST /api/v1/admin/agents/{id}/collectors/{collector_name}/toggle` — enable/disable a specific collector.
- `PUT /api/v1/admin/agents/{id}/collectors/{collector_name}/interval` — set a per-collector frequency override.
- `POST /api/v1/admin/agents/{id}/clear-cache` — request that the agent wipe its local delta-tracker cache on its next heartbeat (needed when server-side data is reset independently of the agent, e.g. a projection table wipe, so the agent's cache no longer reflects what the server actually has).
- `GET/POST/PUT/DELETE /api/v1/admin/targets` (+ `PATCH /admin/targets/{id}/enabled`).
- `GET/POST/PUT/DELETE /api/v1/admin/credentials` (+ `PUT /admin/credentials/{id}/secret`).
- `GET /api/v1/admin/conflicts`, `POST /api/v1/admin/conflicts/{fp_type}/{fp_value}/resolve` — fingerprint-conflict surface (REQ-052).
- `GET /api/v1/admin/audit-log` — read audit trail (REQ-027).

## Request Contract
- `GET /api/v1/admin/agents?after=&limit=&status=` — list.
- `PATCH /api/v1/admin/agents/{id}/approve` — no body; sets `status=approved`. `/disable` sets `disabled`.
- `PATCH /api/v1/admin/agents/{id}` body `{ "name?", "zone?", "enabled?" }` — partial update of agent metadata (REQ-005). Any subset of fields.
- `PUT /api/v1/admin/agents/{id}/config` body `{ "heartbeat_interval_secs", "discovery_interval_secs", "inventory_interval_secs" }` — full replace of agent-level collection intervals (REQ-005); delivered to the agent on its next heartbeat.
- `POST /api/v1/admin/agents/{id}/collectors/{collector_name}/toggle` body `{ "enabled": true|false }` — enable/disable a named collector for the agent (REQ-008).
- `PUT /api/v1/admin/agents/{id}/collectors/{collector_name}/interval` body `{ "interval_secs": <int> }` — per-collector frequency override (REQ-009); delivered via heartbeat config.
- `POST /api/v1/admin/agents/{id}/clear-cache` — no body; sets `clear_trackers_requested_at = now()`. Takes effect on the agent's next heartbeat; no synchronous confirmation it happened.
- `GET /api/v1/admin/targets?after=&limit=&agent_id=` — list.
- `POST /api/v1/admin/targets` body `{ "agent_id", "endpoint", "collector_type", "credential_id?", "label?", "endpoint_kind?", "enabled" }`. `collector_type` is one of `ssh`, `snmp`, `http`, `cert`, `bacnet`, `modbus`, `google-wifi`, `technitium-dns`, `home-assistant`. `endpoint_kind` is `address` (default) or `mac`; for `address`, `endpoint` is a bare host/IP for device-style collector types or a full URL for service-style ones (`technitium-dns`, `home-assistant`); for `mac`, `endpoint` is a MAC address (any separator style; normalized to bare 12-hex and resolved to the device's current IP at config-assembly time). `mac` is rejected (`422`) for the URL-style collectors.
- `PUT /api/v1/admin/targets/{id}` body `{ "endpoint", "collector_type", "credential_id?", "label?", "endpoint_kind?" }` — update a target (REQ-006). `enabled` is toggled via the dedicated endpoint below.
- `PATCH /api/v1/admin/targets/{id}/enabled` body `{ "enabled": true|false }` — toggle target enabled/disabled (REQ-008).
- `DELETE /api/v1/admin/targets/{id}`.
- `GET /api/v1/admin/credentials?after=&limit=` — **metadata only** (`credential_id, name, type, created_at`); never returns `encrypted_blob` or plaintext.
- `POST /api/v1/admin/credentials` body `{ "name", "type", "secret": "<plaintext, write-only>" }` — server encrypts via Data Protection before storage.
- `PUT /api/v1/admin/credentials/{id}` body `{ "name", "type" }` — update credential metadata only (rename / retype, REQ-007). Does not touch the secret.
- `PUT /api/v1/admin/credentials/{id}/secret` body `{ "secret": "<plaintext, write-only>" }` — rotate the stored secret (REQ-007). Server re-encrypts via Data Protection; **returns `204`, never a response body — the secret is never returned**.
- `DELETE /api/v1/admin/credentials/{id}`.
- `GET /api/v1/admin/conflicts?after=&limit=` — list active fingerprint conflicts (REQ-052): pairs of device records that share a fingerprint not yet resolved or excluded.
- `POST /api/v1/admin/conflicts/{fp_type}/{fp_value}/resolve` body `{ "action": "merge", "winner_device_id": "uuid" }` **or** `{ "action": "exclude" }` — `merge` merges the conflicting pair into the winner (delegates to the device merge path, ADR-002); `exclude` records the fingerprint in `excluded_fingerprints` so it is ignored by future auto-matching.
- `GET /api/v1/admin/audit-log?after=&limit=&device_id=&agent_id=&action=` — keyset-paginated audit-log read (REQ-027). `device_id`/`agent_id` filter against the `target_ref`/`actor` columns; `action` filters the `action` column.

## Response Contract
- agents list → `200 { "items": [ { "agent_id", "hostname", "status", "zone", "version", "last_heartbeat", "passive_discovery_mode" } ], "next_cursor" }`.
- approve/disable → `200 { "agent_id", "status" }`.
- agent update (`PATCH /agents/{id}`) → `200 { "agent_id", "hostname", "zone", "status" }`.
- agent config (`PUT /agents/{id}/config`) → `200 { "agent_id", "heartbeat_interval_secs", "discovery_interval_secs", "inventory_interval_secs" }`.
- collector toggle / interval → `200 { "agent_id", "collector_name", "enabled" | "interval_secs" }`.
- clear-cache → `200 { "status": "requested" }`.
- targets list → `200 { "items": [...], "next_cursor" }`; create → `201`; update (`PUT`) → `200`; enabled toggle (`PATCH`) → `200 { "target_id", "enabled" }`; delete → `204`.
- credentials list → `200 { "items": [ { "credential_id", "name", "type", "created_at" } ], "next_cursor" }`; create → `201 { "credential_id" }`; metadata update (`PUT /credentials/{id}`) → `200 { "credential_id", "name", "type" }`; secret rotate (`PUT /credentials/{id}/secret`) → `204` **(no body)**; delete → `204`.
- conflicts list → `200 { "items": [ { "fp_type", "fp_value", "device_a": { "device_id", "hostname", "management_status" }, "device_b": { "device_id", "hostname", "management_status" }, "first_seen" } ], "next_cursor" }`.
- conflict resolve → `200 { "fp_type", "fp_value", "action", "survivor_device_id?" }` (`survivor_device_id` present for `merge`).
- audit-log read → `200 { "items": [ { "id", "occurred_at", "actor", "action", "target_ref", "detail" } ], "next_cursor" }`.

## Pagination
**Keyset** (cursor token passed back as `?after=`): agents on `(created_at DESC, agent_id)`; targets on `(created_at DESC, target_id)`; credentials on `(created_at DESC, credential_id)`; audit-log on `(occurred_at DESC, id)`; conflicts on `(first_seen DESC, fp_type, fp_value)`. Indexes on each tuple.

## Error Responses
Uniform envelope. `401 unauthorized`, `403 forbidden` (non-admin), `404 not_found`, `409 conflict` (e.g. delete a credential still referenced by a target → `409 credential_in_use`; conflict resolve on an already-resolved fingerprint → `409 conflict_already_resolved`), `422 validation_error` (bad collector_type/credential type, bad interval, unknown collector_name, bad resolve action).

## Cross-References
REQ-002, REQ-003, REQ-004, REQ-005, REQ-006, REQ-007, REQ-008, REQ-009, REQ-027, REQ-052. COMP-007, COMP-008, COMP-004, COMP-010. ENTITY-003, ENTITY-004, ENTITY-005. DEC-001, DEC-002. ADR-002.
