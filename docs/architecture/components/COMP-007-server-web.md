---
id: COMP-007
name: Server Web
status: draft
---
## Responsibility

New ASP.NET Core host (`Server.Web` project, D7) — the only new project. References the existing `Server`, `Analysis`, `Core` class libraries. Per ADR-004 (D6): **Razor Pages + HTMX, server-rendered, polling for live data** (no SSE/WebSockets, per DEC-005/constraints #11).

Three surfaces in one host:

1. **UI (Razor Pages + HTMX)**: serves all screens from the UX spec — fleet dashboard, device list/detail, service list/detail, reports (hosts, services, ports, containers, storage, ARP, security posture, patch status, change feed), agent management, collection config, credentials, admin accounts/sessions, bootstrap. HTMX partials poll projection-backed endpoints for liveness. URL is the source of truth for navigation state (constraints #10): active tab/filter/selected-device/pagination cursor encoded in path + query.
2. **Agent API** (`/api/v1/agent/*`): register, heartbeat, facts ingest. Hosts the ingest controller that delegates to COMP-003.
3. **Admin/Device/Reporting JSON APIs** (`/api/v1/*`): consumed by HTMX partials and any tooling. Reads proxy projection tables (read-only, DEC-005); writes go through Device Registry / config / credential services.

Validates user sessions (COMP-008) for all UI + admin/device/reporting routes; validates agent API keys for `/api/v1/agent/*`.

## Interfaces

- **Inbound HTTP (browser)**: Razor Pages + HTMX partial endpoints. HTTPS in deployment (assumption #8).
- **Inbound HTTP (agents)**: `/api/v1/agent/*`.
- **→ COMP-008 Auth (in-process)**: session validation middleware (cookie), agent API-key middleware, RBAC checks (REQ-002).
- **→ COMP-006 Projections (in-process, read-only)**: report/list queries.
- **→ COMP-004 Device Registry (in-process)**: device list/detail, merge, promote.
- **→ COMP-009 Database (in-process)**: agent config, collection targets, credentials (encrypted via COMP-008 Data Protection), audit log, user store.

## Dependencies

- COMP-008 Server Auth
- COMP-006 Server Projections
- COMP-004 Server Device Registry
- COMP-003 Server Ingest
- COMP-009 Database

## Integration Test Boundaries

- **Session-gated UI (HTTP)**: request a report page without a session cookie; assert redirect to login. With a viewer session, assert read-only pages render and admin actions are hidden/forbidden (REQ-002 RBAC).
- **HTMX polling partial (HTTP)**: request a dashboard partial; assert it returns an HTML fragment from projection data within the performance budget (REQ-026: <1s for ≤500 devices).
- **Read-only report invariant (DB)**: assert reporting endpoints issue only SELECTs against `proj_*` / `facts_history` (no writes) — DEC-005.
- **URL-state round-trip (HTTP)**: deep-link a filtered/paginated report URL; assert the page renders the same state on fresh load (constraints #10).
