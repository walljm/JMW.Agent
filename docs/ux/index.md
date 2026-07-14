---
agent: sdev-ux
date: 2026-06-06
iteration: 3
ux_depth_level: standard
domain_complexity: complex
revision_id: 8
status: draft
---

# UX Design Overview — JMW Agent Facts Server

## UX Depth Level

**standard** — per orchestrator briefing. Phase 1a deep-dive machinery (complexity triage table, per-feature ⚑ Complex deep dives) is intentionally NOT produced (Deep-level only). Complex feature areas (notably All Hosts) receive careful prose treatment within the standard template.

## Domain Complexity

**complex** — The product surfaces a deep expert vocabulary (ARP, OUI, LLDP/CDP, mDNS, SMART, fingerprinting, fact provenance, multi-source merge precedence). Three traits force the complex classification:

1. **Expert vocabulary, no simplification mandate.** Both personas are technical; PERSONA-001 explicitly "expects a professional, information-dense UI — does not need consumer-grade simplification." Labels must preserve domain terms verbatim.
2. **High information density.** Device Detail (REQ-012) has ~17 data sections; the fleet has dozens of cross-cutting reporting views.
3. **Provenance is load-bearing.** REQ-035 and REQ-038 require per-fact source attribution and a defined merge precedence (`agent-direct > lldp > mdns > nbns > arp/dhcp`). The UI must surface *where a value came from* and *how confident* the device profile is — this is not optional decoration; it is how an operator decides whether to trust a discovered device.

Complex-domain obligations applied throughout: glossary terms preserved in labels, provenance surfaced in the UI, undo limitations acknowledged (promotion, credential rotation), keyboard-first considered for high-frequency scanning, confirmation strategy designed to avoid alert fatigue.

## Design Influences

- **Network operations consoles** (the operator's existing mental reference): dense tables, status-at-a-glance color coding, drill-down from summary to detail. NOT consumer dashboards.
- **Read-from-projection model** (DEC-005): all reporting screens are read-only views over current-state projection tables. No inline editing on reporting pages; editing lives exclusively in the Admin area.
- **Single-operator deployment** (REQ-024): no multi-tenant concerns, no collaboration/concurrency UI, no per-user customization. Two fixed roles only (REQ-002).

## Design Rationale (summary)

The product is a monitoring console for one expert operator (plus an occasional read-only viewer). The dominant activities are *scanning* (is anything wrong across the fleet?) and *investigating* (drill into one device/cert/port). The IA and interaction model optimize for those two loops:

- **Scan loop:** Fleet Dashboard → click a summary card → land on a pre-filtered reporting table. The dashboard-card-to-filtered-view link contract (REQ-010) drives a shared filter-param URL scheme so every card deep-links into the matching report.
- **Investigate loop:** any table row → entity detail page (≤ 3 clicks from dashboard, per success criterion 3).

The single genuinely novel screen is **All Hosts** (REQ-037), which unifies managed and discovered devices in one table with provenance/confidence cues and an inline promotion path. It gets dedicated attention in the usability design.

## Navigation Model

Role-aware **left sidebar** with grouped sections (rationale in `information-arch.md`). The Admin group is omitted entirely for the Read-Only Viewer role (REQ-002).

## Artifacts in this directory

- `usability-design.md` — interaction pattern decisions (Phase 1a)
- `information-arch.md` — IA structure and labeling rationale (Step 2a-ii)
- `flows/FLOW-NNN.md` — user flow records (Step 2a-ii)
- `screen-specs.md` — per-screen specifications (Step 2a-ii)
- `brand-brief.md`, `design-conventions.md`, `accessibility.md` — design direction (Phase 2)
- `mockups/` — design system + HTML mockups (Phase 2 + Phase 3)
- `responsive.md`, `error-states.md` — supporting design docs
