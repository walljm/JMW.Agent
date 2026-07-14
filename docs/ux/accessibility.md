---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 10
status: draft
---

# Accessibility — JMW Agent Facts Server

## Target Level

**WCAG 2.1 AA.** Achievable for a data-dense internal tool with two technical users; AAA contrast is not required but the dark palette's primary text comfortably exceeds AA.

## Color & Contrast

- Body/data text (`--text` on `--surface`/`--bg`) targets ≥ 7:1 in both modes; secondary text (`--text-dim`) targets ≥ 4.5:1. Status text on its `*-bg` chip targets ≥ 4.5:1.
- **No reliance on color alone** (AA 1.4.1): every status carries a shape glyph (square/triangle/circle) and a text label in addition to color; management status uses solid-vs-dashed badges; problem rows use a left rail AND the row's status pills. A grayscale render still distinguishes ok/warn/crit.
- The focus ring uses `--focus` (cyan/blue), deliberately distinct from `--accent` (green) so focus remains visible even on accent-colored elements.

## Keyboard Navigation

- All interactive elements (nav items, sortable headers, filter controls, row links, buttons, tabs, modal controls, mode toggle) are reachable and operable by keyboard in a logical order.
- **Tables:** column headers are focusable buttons (sort on Enter/Space). Row drill-down is a focusable link/row; Enter opens detail.
- **Tabs (entity detail):** arrow-key roving tabindex within the tablist; Enter/Space activates; `aria-selected` on the active tab; each panel `aria-labelledby` its tab.
- **Modals (target/credential/promotion):** focus moves into the modal on open, is trapped within while open, returns to the launching control on close; Escape cancels; the primary action is reachable without leaving the keyboard.
- **Scan path is mouse-free:** global search/filter is reachable via keyboard; the dashboard-card → filtered-view links are standard focusable anchors. (Keyboard-first was committed in usability design for the high-frequency scan loop.)

## Focus Management

- Visible focus on every focusable element (`:focus-visible` outline using `--focus`).
- On navigation to a new page, focus moves to the page `<h1>`/main landmark.
- On opening a modal, focus moves to its first field or heading; on close, focus restores to the trigger.
- Per-section error/retry controls are focusable so a keyboard user can retry a failed section.

## Screen Reader / Semantics

- Semantic landmarks: `<nav>` sidebar, `<main>` content, `<header>` page head; one `<h1>` per page, logical heading hierarchy.
- Tables use `<table>`/`<thead>`/`<th scope="col">`; sortable headers expose `aria-sort`.
- Status pills include visible text (not icon-only), so the status is announced; decorative shape glyphs are CSS `::before` (not announced) — meaning is in the text.
- Management badges include the literal word "Managed"/"Discovered" as text.
- Source tags are a list with accessible names (e.g., "Discovery source: mDNS").
- Auto-refresh updates announce politely via an `aria-live="polite"` region for the "last updated" timestamp; refresh must not move focus or reorder rows under the user.
- Live error banners use `role="alert"` (assertive) for failures and `aria-live="polite"` for stale-data notices.
- Form fields have associated `<label>`s; validation errors are linked via `aria-describedby` and announced.

## Alternative Text

- The product is data-driven, not image-heavy. The wordmark/glyph is decorative (`aria-hidden`) since the product name appears as text. Capacity bars and status glyphs are paired with text values/labels, so no `alt` text is load-bearing.

## Motion

- Skeleton shimmer and transitions respect `prefers-reduced-motion: reduce` (animations disabled, transitions removed) — implemented in `design-system.css`.

## Resilience interplay (REQ-028)

- Error and stale states are conveyed by text + color + icon, never color alone, and are exposed to assistive tech via live regions — a screen-reader user learns the data is stale or a section failed.
