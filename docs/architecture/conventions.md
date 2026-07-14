---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Architecture Conventions (Binding)

All downstream agents (developer, reviewers, QA) enforce this document.

## Solution / Project Layout (D7)

- Existing projects remain: `Core`, `Collector`, `Agent`, `Analysis`, `Server`.
- New: `Server.Web` (ASP.NET Core) — references `Server`, `Analysis`, `Core`. The only new project.
- Tests mirror the project under test: `<Project>.Tests` (e.g. `Server.Web.Tests`).
- Source under `scratch/src/`; deploy artifacts under `scratch/deploy/`; schema in `scratch/Schema.sql`.

## Naming

- **Types/public members**: PascalCase. **Locals/params**: camelCase. (C# idiom; do not rename existing code.)
- **Components** referenced in docs: `COMP-NNN`; **entities** `ENTITY-NNN`; **APIs** `API-NNN`; **ADRs** `adr-NNN`; **deps** `DEP-NNN`.
- **Service classes**: `<Domain>Service` (e.g. `CredentialService`, `AgentService`). **DB access**: `<Domain>Repository` (e.g. `DeviceRepository`). Repositories own SQL; services own orchestration.
- **Razor Pages**: PascalCase page name matching the route segment (`Pages/Devices/Index.cshtml` → `/devices`). **HTMX partial endpoints**: suffix `Partial` on the handler (`OnGetHostsTablePartial`) and return `PartialViewResult`/`Content` HTML — never JSON.
- **DTOs**: `<Thing>Request` / `<Thing>Response` records (e.g. `LoginRequest`, `AgentRegisterResponse`). API models are separate from DB rows (no leaking schema, per API-design layer separation).
- **DB**: `snake_case` tables/columns (matches existing `Schema.sql`). Projection tables `proj_*`. Wire JSON: `snake_case` property names.

## API Conventions

- **Versioning**: all endpoints under `/api/v1/`. Breaking changes → new version path. The old `scratch/` `/devices/identify` + `/ingest/{deviceId}` are removed (no external consumers; ADR-001).
- **Two principals**: agent endpoints (`/api/v1/agent/*`) use `Authorization: Bearer <api_key>`; all other endpoints use the session cookie. Enforced by middleware, never per-handler ad hoc (security.md).
- **Uniform error envelope** (every endpoint, every error):
  ```json
  { "error": { "code": "snake_case_code", "message": "human domain text" } }
  ```
  Codes are domain terms (`device_not_found`, `not_approved`, `credential_in_use`), never raw SQL/exception text. Standard statuses: `400` validation/bad-batch, `401` unauthorized, `403` forbidden (RBAC/approval), `404` not-found, `409` conflict, `413` payload-too-large, `422` unprocessable (bad cursor/filter), `429` rate-limited, `500` internal (generic message, details only to logs).
- **Input validation**: validate at the controller/page-handler boundary with explicit checks (or `System.ComponentModel.DataAnnotations` on request records); return `400`/`422` with the uniform envelope. No raw exceptions to the client.
- **Pagination**: all list endpoints use **keyset (cursor-based)** pagination — opaque base64 cursor encoding the last sort tuple; response `{ "items": [...], "next_cursor": "opaque|null" }`. **Never offset/limit.** Every sort tuple must be backed by an index (see schema-additions.md / existing `proj_*` indexes).
- **Read-only reports** (DEC-005): reporting endpoints issue only SELECTs against `proj_*` / `facts_history`.
- **Timestamps**: ISO-8601 UTC in JSON; `TIMESTAMPTZ` in DB.

## Error Handling & Resilience

- **Agent→server**: idempotent ret*able* operations (heartbeat, facts) retry with bounded exponential backoff + jitter. On `facts` send failure, the delta tracker **rolls back** so unsent facts re-send next cycle (ADR-001). `403 not_approved` is not retried as a transient — the agent waits for approval (polled via heartbeat).
- **Server→DB**: transient connection failures surface as `503` (UI) / `500` (API) with the uniform envelope; no retry loops that hold connections.
- **Graceful degradation**: passive raw-socket collectors fall back to snapshot ARP and warn (ADR-003). DB unavailable → health check fails (observability.md); UI shows an error state (REQ-028), not a stack trace.
- **User-facing errors**: friendly message + correlation id; never raw exception/SQL (REQ-028).

## Logging

- Structured logging via `Microsoft.Extensions.Logging` (ILogger). One scheme across all endpoints/services.
- Levels: `Error` (handled failures + 5xx), `Warning` (degraded mode, retries, auth failures), `Information` (request summary, agent register/approve, merge events), `Debug` (dev only).
- Every request logs: method, path, principal type (`user:<id>` / `agent:<id>`), status, elapsed ms, correlation id.
- **Never log**: credential plaintext, API keys, session cookies, password material. Fingerprints/MACs are allowed (infrastructure identifiers).
- Security-relevant events also written to `audit_log` (REQ-027): login, logout, agent approve/disable, target/credential CRUD, device merge/promote.

## Testing Conventions

- xUnit. Test names: `MethodOrScenario_Condition_ExpectedResult`.
- **Integration tests** target the boundaries declared in each `COMP-NNN.md`. DB tests run against a real PostgreSQL (Testcontainers or a disposable schema), not mocks — raw SQL must be exercised.
- Bug fixes add table-driven tests covering the whole error class (per project testing rules).
- No `git stash` to attribute test failures.

## Static Analysis (ADR-007)

`Directory.Build.props`: `EnableNETAnalyzers=true`, `AnalysisLevel=latest-Recommended`, `TreatWarningsAsErrors=true`, `Nullable=enable`, `EnforceCodeStyleInBuild=true`. CI: `dotnet build -warnaserror` + `dotnet format --verify-no-changes`. No `!` null-suppression; handle nulls explicitly. No `ConfigureAwait(false)`.
