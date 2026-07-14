---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 12
status: draft
---

# Responsive Behavior — JMW Agent Facts Server

Both personas use a desktop browser (PERSONA-001/002); PERSONA-001 "may occasionally view dashboards from a tablet." The app is desktop-first; the single breakpoint exists to keep the dashboard and key tables usable on a tablet, not to deliver a polished phone experience (out of scope — no mobile app, REQ index). Breakpoint pixel values are defined as media queries in `design-system.css`; this doc describes what *changes*.

## Desktop (default, ≥ breakpoint)

- Two-column shell: fixed left sidebar + scrolling main. Full table column sets visible. Dense layout as designed.

## Below the breakpoint (tablet / narrow window)

- **Sidebar collapses to an off-canvas drawer** (`.sidebar` becomes fixed and translated off-screen; `.open` slides it in). A menu control toggles it. Main content takes full width.
- **Reporting tables hide low-priority trailing columns** (6th column onward) — both the `<th>` headers and `<td>` cells are hidden together so columns stay aligned (the header+body pairing is the corrected rule in `design-system.css`). The high-value identity/status columns remain; the full column set is always available on the entity Detail page. This keeps All Hosts/Security scannable on a tablet without horizontal scroll.
- **Dashboard card grid reflows** automatically (the grid is `auto-fill minmax`), so cards wrap to fewer columns; no rule change needed.
- **Toolbars wrap** (filters already `flex-wrap`), so filter controls stack rather than overflow.
- **Modals** (target/credential/promotion) use `width: min(560px, 92vw)`, so they fit narrow viewports.

## Touch vs pointer

- Row click targets and inline action buttons are large enough for touch; hover-only affordances are not load-bearing (status meaning is always in text+shape, not a hover tooltip). Sort/filter remain tap-operable.

## Not addressed (out of scope)

- Phone-width single-column table transforms (card-per-row) are not built — no mobile requirement. The tablet column-collapse is the supported small-screen mode.
