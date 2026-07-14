---
id: adr-004
title: Razor Pages plus HTMX with polling liveness
status: draft
date: 2026-06-04
---
## Context

Iteration 2 adds a web UI (D6, D7) on a C# .NET 10 codebase (constraints #1). The UI is operator-facing, data-dense (reports, tables), single-operator, desktop-first, and reads a projection-backed read model that updates as facts arrive. DEC-005 already settled that reporting pages are read-only from projection tables and that liveness is polling-based (no SSE/WebSockets, constraints #11).

## Alternatives Considered

1. **SPA (React/Blazor WASM) + JSON API.** Rejected: heavier toolchain and a second language/build; client-side state management for navigation conflicts with the URL-as-source-of-truth requirement (constraints #10) unless carefully managed; overkill for a single-operator read-mostly tool. Adds a large dependency surface (constraints #7).
2. **Blazor Server (SignalR).** Rejected: SignalR is a persistent WebSocket connection — directly contradicts DEC-005's "no SSE/WebSockets, polling only," and adds server-side circuit state per user.
3. **Razor Pages + HTMX, server-rendered, polling (chosen, D6).** Server renders HTML; HTMX swaps partials and polls projection-backed endpoints for liveness.

**Strongest argument against the chosen option:** HTMX is a relatively young library and a comparatively small ecosystem versus React; complex interactive widgets (rich client-side filtering, drag-drop) are harder than in an SPA. Accepted because the UI is table/report-centric with simple interactions, the data is read-mostly, and avoiding a JS build pipeline is a net simplification for a single-maintainer C# project. HTMX is a single ~14KB dependency with no transitive deps (see DEP record).

## Decision

ASP.NET Core **Razor Pages + HTMX**, server-rendered. HTMX partials poll projection-backed endpoints for live data (no push). Navigation state lives in the URL (path + query), per constraints #10. Served over HTTPS in deployment.

## Rationale

Matches the existing C# stack (one language, one build), keeps the dependency surface minimal (constraints #7), and aligns with the already-approved polling model (DEC-005). Server-side rendering makes URL-driven navigation natural and keeps logic in C# where the rest of the system lives.

## Consequences

- Live data has polling latency (acceptable per constraints #11; budget <1s page load for ≤500 devices, REQ-026).
- HTMX partial endpoints must return HTML fragments (a second response shape alongside the JSON APIs) — conventions.md fixes the partial-endpoint naming/return pattern.
- No client-side framework; complex future interactions may need targeted JS — accepted as a future cost, not designed now.
