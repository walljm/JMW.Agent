---
id: API-001
path: /api/v1/agent/*
method: POST
status: draft
---
## Description

Agent-facing endpoints (machine principals, API-key auth). Replaces the old `POST /devices/identify` + `POST /ingest/{deviceId}` pair with a single identity-free ingest (ADR-001, D2). Auth: `Authorization: Bearer <api_key>` except `register` (which establishes the key). Endpoints:

- `POST /api/v1/agent/register` — open self-registration with admin approval (D4).
- `POST /api/v1/agent/heartbeat` — liveness + config pull.
- `POST /api/v1/agent/facts` — identity-free fact ingest.

## Request Contract

**`POST /api/v1/agent/register`** (no auth):
```json
{ "agent_id": "uuid", "hostname": "str", "zone": "str?", "version": "str", "passive_discovery_mode": "full|degraded" }
```

**`POST /api/v1/agent/heartbeat`** (Bearer):
```json
{ "agent_id": "uuid", "passive_discovery_mode": "full|degraded" }
```

**`POST /api/v1/agent/facts`** (Bearer, `Content-Encoding: gzip`):
```json
{ "agent_id": "uuid", "collected_at": "iso8601",
  "fact_batches": [ { "fingerprints": [{"type":"mac","value":"001a2b3c4d5e"}],
                      "facts": [ /* Fact[] with placeholder device root */ ] } ] }
```

## Response Contract

- **register** → `201` `{ "agent_id": "uuid", "status": "pending", "api_key": "<plaintext-once>" }`. Key returned exactly once; only its hash is stored.
- **heartbeat** → `200` `{ "status": "pending|approved|disabled", "config": { "interval": 30, "max_concurrency": 4, "targets": [...], "services": [...] } }` (config present only when `approved`, DEC-001).
- **facts** → `202 Accepted` `{ "accepted_batches": N, "resolved_devices": [{ "fingerprints_hash": "...", "device_id": "uuid", "is_new": bool }] }`. (device_id is informational telemetry; the agent does NOT persist or key delta tracking on it — ADR-001.)

## Pagination
N/A (no list endpoints in this group).

## Error Responses
Uniform envelope (see conventions.md): `{ "error": { "code": "snake_case", "message": "domain text" } }`.
- `400 invalid_batch` — no valid fingerprints after normalization, or batch >50 000 facts.
- `401 unauthorized` — missing/invalid API key.
- `403 not_approved` — agent status ≠ approved (mirrors Go app).
- `409 agent_exists` — register with an already-registered `agent_id` (returns existing status, idempotent-friendly).
- `413 payload_too_large` — body exceeds size cap.

## Cross-References
REQ-003, REQ-004, REQ-005, REQ-008, REQ-009, REQ-029, REQ-038, REQ-052. COMP-001, COMP-002, COMP-003, COMP-008. ENTITY-003, ENTITY-006. ADR-001, ADR-002, ADR-003. DEC-001.
