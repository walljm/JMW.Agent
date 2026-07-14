---
agent: sdev-architecture
iteration: 2
revision_id: 23
date: 2026-06-04
status: draft
---

# Architecture — JMW Agent Facts Server (Iteration 2)

This iteration adds a web UI for agent management, collection configuration, and network-monitoring reports, and makes a set of owner-approved changes to the agent/server contract and the agent runtime model. It builds on the existing `scratch/` codebase (the production codebase, D7).

## System Overview

The system collects **facts** about network nodes via long-running **agents**, ingests them into a single **PostgreSQL** store, maintains current-state **projection** tables, and serves an operator **web UI** plus agent/admin/reporting APIs from a new ASP.NET Core host.

After each ingest batch is committed, the Discovery Materializer (COMP-010, ADR-008) scans projection columns carrying foreign-device fingerprints (ARP MAC entries, DHCP lease MACs, LLDP neighbor IDs) and creates `discovered` device records for any fingerprint not already in the device registry. Auto-merge (ADR-002) then applies to these materialized devices on subsequent fingerprint overlap.

### Components (see `components/`)

| ID | Component | Responsibility |
|----|-----------|----------------|
| COMP-001 | Agent | Long-running daemon; local + remote-target collection; submits `{fingerprints, facts}` batches; no device_id cache. |
| COMP-002 | Agent.PassiveDiscovery | In-process event-driven listeners (mDNS, SSDP, WS-Discovery, ARP, LLMNR); privilege-aware. |
| COMP-003 | Server.Ingest | Receives batches; resolves identity; rewrites fact IDs; appends history; routes projections. |
| COMP-004 | Server.DeviceRegistry | Fingerprint resolution; **auto-merge** on overlap; device lifecycle. |
| COMP-005 | Server.Analysis | Normalizers + derivations (agent-side + cross-device server-side). |
| COMP-006 | Server.Projections | GenericProjection current-state tables (read model). |
| COMP-007 | Server.Web | New ASP.NET Core host; Razor Pages + HTMX UI; agent/admin/reporting/auth APIs. |
| COMP-008 | Server.Auth | User sessions (cookies, RBAC) + agent API keys + credential encryption (Data Protection). |
| COMP-009 | Database | Single PostgreSQL: facts, projections, registry, config, credentials, auth, audit. |
| COMP-010 | Server.DiscoveryMaterializer | Post-ingest pass (ADR-008): scans projection columns carrying foreign-device fingerprints and materializes `discovered` device records for any fingerprint not already in the registry. |

### Component Interaction Diagram

```
                         ┌──────────────────────────────────────────────┐
                         │                Agent (daemon)                  │
                         │  COMP-001  ── collection cycle (local+remote)   │
   network devices ◀────▶│  COMP-002  ── passive discovery listeners       │
                         │     │ emits {fingerprints[], facts[]} batches    │
                         └─────┼──────────────────────────────────────────┘
                               │  HTTPS  POST /api/v1/agent/{register,heartbeat,facts}
                               │  Bearer api_key, gzip JSON
                               ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │                       Server.Web (ASP.NET Core)  COMP-007          │
        │  ┌──────────────┐  ┌───────────────┐  ┌──────────────────────────┐ │
        │  │ Razor+HTMX UI│  │ Agent API     │  │ Admin/Device/Report/Auth │ │
        │  └──────┬───────┘  └──────┬────────┘  └──────────┬───────────────┘ │
        │         │ session-cookie  │ api-key              │                  │
        │         ▼                 ▼                      ▼                  │
        │      COMP-008 Auth   COMP-003 Ingest        COMP-004 Registry       │
        │                          │  resolve id ─────────▶│                  │
        │                          │  ID rewrite           │ auto-merge       │
        │                          ▼                       ▼                  │
        │      COMP-005 Analysis ─▶ COMP-006 Projections                      │
        └──────────────────────────────┬────────────────────────────────────┘
                                        │ NpgsqlDataSource (raw SQL, no ORM)
                                        ▼
                         ┌──────────────────────────────────┐
                         │   PostgreSQL  COMP-009            │
                         │  facts_history · proj_* ·         │
                         │  devices · device_fingerprints ·  │
                         │  device_aliases · agents ·        │
                         │  targets · credentials            │
                         │  users · user_sessions · audit_log│
                         └──────────────────────────────────┘
```

### Browser read path (HTMX polling)
```
browser ──GET (poll)──▶ Server.Web report endpoint ──SELECT──▶ proj_* / facts_history ──HTML fragment──▶ browser
```

## Technology Choices

| Concern | Choice | Rationale / ADR |
|---------|--------|-----------------|
| Language/runtime | C# .NET 10 | constraints #1 (match existing codebase) |
| Web framework | ASP.NET Core, Razor Pages + HTMX | ADR-004 (D6); polling liveness per DEC-005 |
| Database | PostgreSQL (single store) | ADR-005 (D9); schema in `scratch/Schema.sql` |
| DB access | Npgsql `NpgsqlDataSource`, raw SQL, no ORM | constraints #3, DotNet guidance, DEP-003 |
| User auth | Server-side sessions, httpOnly cookies | constraints #6 (no JWT); `security.md` |
| Agent auth | API keys (hash stored), approval-gated | REQ-003/004; `security.md` |
| Secrets at rest | .NET Data Protection (`bytea`) | DEC-002, D10, DEP-004 |
| Deployment | Docker + systemd, single host | D8; `deployment.md` |
| Static analysis | Roslyn analyzers, warnings-as-errors, `Nullable enable` | ADR-007; `conventions.md` |

**Research/uncertainty:** live web research was not performed in this environment. Exact .NET 10 patch level, Npgsql/.NET-10 compatibility, and the current HTMX release are flagged in the dependency records (DEP-001..004) for confirmation at implementation time. All other choices are constrained by `constraints.md` / owner decisions D1–D10, not open technology selection.

## ADR Summary (see `adrs/index.md`)

| ADR | Decision |
|-----|----------|
| adr-001 | Ingest contract: server resolves device identity; delta tracking re-keyed onto source identity (D2) |
| adr-002 | Auto-merge on fingerprint overlap; supersedes conservative DeviceRegistry stance (D1) |
| adr-003 | Agent as long-running daemon with in-process passive discovery (D3, D5) |
| adr-004 | Razor Pages + HTMX, polling liveness (D6) |
| adr-005 | Single PostgreSQL store for all server data (D9) |
| adr-006 | Large-scale mechanisms inherited but deferred at this scale |
| adr-007 | Roslyn analyzers as mandatory static analysis |
| adr-008 | Discovery Materializer: post-ingest pass materializes `discovered` devices from foreign-device fingerprints in projections (COMP-010) |

## What Changed from `scratch/` Current State

1. **Ingest contract (breaking)**: `POST /devices/identify` + `POST /ingest/{deviceId}` → single `POST /api/v1/agent/facts` with `{fingerprints, facts}`; server resolves identity and rewrites fact IDs (ADR-001). `FactBatch` wire format changes (ENTITY-006).
2. **Delta tracking** re-keyed from server `device_id` onto agent-local source identity (ADR-001).
3. **DeviceRegistry** auto-merges on fingerprint overlap instead of flagging (ADR-002); new `device_aliases`.
4. **Agent** becomes a long-running daemon with in-process passive discovery and graceful raw-socket degradation (ADR-003).
5. **New `Server.Web` project** (the only new project) hosting UI + APIs (D7).
6. **New server-managed config/auth tables**: `agents`, `targets` (originally shipped as separate `collection_targets`/`service_targets` tables for device- vs service-style polling; merged into one `targets` table since the split was accidental, not conceptual — see ENTITY-004), `credentials`, `users`, `user_sessions`, `audit_log` (schema-additions.md).
7. **Scale mechanisms** (multi-instance affinity, partitioning) explicitly deferred to a single instance (ADR-006).

## Document Map

- `components/` — COMP-001..010
- `data-model/` — ENTITY-001..006
- `api/` — API-001..005
- `adrs/` — adr-001..008 + `adrs/index.md`
- `dependencies/` — DEP-001..004
- `conventions.md` — binding conventions (naming, error envelope, partials, logging, tests)
- `security.md` — auth, RBAC, secrets
- `observability.md` — logging, metrics, health
- `schema-additions.md` — DB schema deltas for iteration 2
- `deployment.md` — Docker + systemd + key-ring persistence
- `api-spec.md` / `api-spec.yaml` — API design notes + OpenAPI 3.1 contract
