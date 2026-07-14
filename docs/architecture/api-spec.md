---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# API Design Notes

Human-readable companion to `api-spec.yaml` (the authoritative OpenAPI 3.1 contract). Per-endpoint detail lives in the `api/API-NNN.md` records; this document covers cross-cutting design and the changelog.

## Principals and base path

- All endpoints are under `/api/v1/` (conventions.md). One version this iteration.
- **Agent endpoints** (`/api/v1/agent/*`): Bearer API-key auth (`agentApiKey`). `register` is unauthenticated (it establishes the key).
- **User endpoints** (everything else): session-cookie auth (`userSession`). RBAC: `/api/v1/admin/*` and device merge/promote require `admin`; device read + reports allow `viewer` and `admin`.

## Cross-cutting conventions (see conventions.md)

- **Error envelope** (every error, every endpoint): `{ "error": { "code": "snake_case", "message": "..." } }`. Codes are domain terms, never raw SQL.
- **Status codes**: 400 validation/bad-batch · 401 unauthorized · 403 forbidden (RBAC/approval) · 404 not-found · 409 conflict · 413 payload-too-large · 422 unprocessable (bad cursor/filter) · 429 rate-limited · 500 internal.
- **Pagination**: every list endpoint uses **keyset** pagination — `?after=<opaque>&limit=` → `{ items, next_cursor }` (the returned `next_cursor` is passed back as `?after=` on the next request, matching the UX information-arch). No offset/limit. Each sort tuple is index-backed (schema-additions.md / existing `proj_*` indexes).
- **Validation**: at the handler boundary; failures return 400/422 with the envelope.
- **Timestamps**: ISO-8601 UTC.
- **Reporting endpoints are read-only** (DEC-005): SELECT-only against `proj_*` / `facts_history`.

## API record map

| API | Group | Endpoints |
|-----|-------|-----------|
| API-001 | Agent ingest | `POST /agent/register`, `POST /agent/heartbeat`, `POST /agent/facts` |
| API-002 | Device management | `GET /devices`, `GET /devices/{id}`, `POST /devices/{id}/merge`, `POST /devices/{id}/promote` |
| API-003 | Admin | `GET /admin/agents`, `PATCH /admin/agents/{id}/approve`, `PATCH /admin/agents/{id}`, `PUT /admin/agents/{id}/config`, `POST /admin/agents/{id}/collectors/{name}/toggle`, `PUT /admin/agents/{id}/collectors/{name}/interval`, `GET/POST/PUT/DELETE /admin/targets` (+ `PATCH /admin/targets/{id}/enabled`), `GET/POST/PUT/DELETE /admin/credentials` (+ `PUT /admin/credentials/{id}/secret`), `GET /admin/conflicts`, `POST /admin/conflicts/{fp_type}/{fp_value}/resolve`, `GET /admin/audit-log` |
| API-004 | Reporting | `GET /report/{hosts,services,services/{id},ports,containers,storage,arp,security,certs,patches,changes}` |
| API-005 | Auth | `POST /auth/login`, `POST /auth/logout`, `GET /auth/me` |

## Key design decisions reflected in the contract

1. **Identity-free ingest (ADR-001, D2)**: `POST /agent/facts` carries `{agent_id, collected_at, fact_batches[{fingerprints, facts}]}`. The server resolves identity and rewrites fact IDs. The response returns `resolved_devices[]` as **informational telemetry only** — the agent must not persist or key delta tracking on it. The old `POST /devices/identify` and `POST /ingest/{deviceId}` are **removed**.
2. **Agent approval gate (D4)**: register returns `pending` + a one-time API key (hash stored); facts/heartbeat from a non-approved agent return `403 not_approved`.
3. **Credential secrecy (REQ-007, DEC-002)**: `POST /admin/credentials` accepts a write-only `secret`; list/read return metadata only — the contract never exposes `encrypted_blob` or plaintext.
4. **Merge/promote (ADR-002, REQ-052/053/036)**: `POST /devices/{id}/merge` and `/promote` mutate via the registry; alias device IDs resolve forward on `GET /devices/{id}`.
5. **HTMX partials**: the same data is also served as HTML fragments from Razor Page handlers (not in this OpenAPI doc — they return HTML, not JSON; see conventions.md). The JSON endpoints here are the contract for tooling and the structured layer the partials read.

## Changelog (vs. `scratch/` agent↔server contract)

| Change | Type | Migration |
|--------|------|-----------|
| Remove `POST /devices/identify` | breaking | No external consumers; agents updated in lockstep (assumption #4) |
| Remove `POST /ingest/{deviceId}` | breaking | Replaced by `POST /api/v1/agent/facts` |
| `FactBatch` drops `DeviceId`, gains `fingerprints[]` per batch + multi-batch envelope | breaking | ENTITY-006; agent+server deploy together |
| Add `/api/v1/agent/register`, `/heartbeat` | additive | New self-registration flow (D4) |
| Add device merge/promote, admin, reporting, auth groups | additive | New UI surfaces |

All breaking changes are confined to the internal agent↔server contract for a single-operator deployment; there are no third-party API consumers (out-of-scope: public REST API).

## Validation

`api-spec.yaml` validated as OpenAPI 3.1.0: 27 paths, all `$ref`s resolve, every operation has a `responses` block. This document is kept consistent with the YAML; the YAML is authoritative.
