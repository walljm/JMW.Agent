---
agent: sdev-02a-ux-designer-phase1a
date: 2026-04-27
status: draft
ux_depth_level: standard
domain_complexity: standard
revision_id: 1
---

# UX — Index

## Depth & Complexity

- **`ux_depth_level: standard`** — single persona, well-understood domain (network monitoring dashboard), no novel interaction problems requiring per-feature deep dives.
- **`domain_complexity: standard`** — network monitoring + IoT discovery is a mature category with established conventions (status lights, device tables, sparklines, topology graphs). Glossary terms are technical but not specialist; Boss is the persona and a software engineer. No specialist-vocabulary or expert-tooling adaptations are required.

## Implementation Stack Constraint (Boss-stated)

- Plain HTML + Go templates server-rendered.
- Vanilla JS for interactivity. **htmx is permitted** for partial-render islands (status badges, lists, summary tiles, log appends) but is not required — vanilla `fetch()` + `setInterval` is the equivalent fallback.
- **No SPA framework** (no Angular / React / Svelte / Preact / Vue). No SSE or WebSockets in MVP — polling only.
- Dark mode is the default theme; light mode is a later concern, not gated for MVP.

## Phase Artifacts

| File | Phase | Purpose |
|---|---|---|
| `index.md` | meta | This file. Depth/complexity classification and stack constraints. |
| `usability-design.md` | 1a | Interaction pattern decisions, component rationale, density strategy, error recovery. |
| `information-architecture.md` | 1b | Navigation structure, screen list, screen specs (produced after this phase). |
| `design-conventions.md` | 1b | Named patterns extracted from 1a + 1b for reuse. |
| `wireframes/` | 2 | Low-fidelity layouts. |
| `mockups/` | 3 | High-fidelity dark-mode visuals. |

## Personas

- **PERSONA-001** — Home-Lab Operator (Boss). Sole user. Highly technical software engineer. Daily glance + reactive drill-in + occasional maintenance.
