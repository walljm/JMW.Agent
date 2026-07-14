---
agent: sdev-ux
date: 2026-06-04
iteration: 3
revision_id: 11
status: draft
---

# Design Conventions — JMW Agent Facts Server

Binding usage rules for the approved "Consumer Tech" direction — a friendly, modern dashboard aesthetic in the spirit of Linear, Vercel, Raycast, and Apple HIG. **Token values live in `design-system.css`** — this file references token names and never restates hex/px values.

## Visual Direction

- **Mood:** Friendly, calm, modern consumer/prosumer software. Reads as a well-designed product dashboard, not a sysadmin terminal.
- **Surfaces:** Dark navy/slate by default (slate-900 page, slate-800 panels) — dark but spacious, not near-black. Cards and panels sit one step lighter than the page with subtle elevation (`--shadow-sm`/`--shadow-md`) and gently rounded corners (`--radius-lg` 8px cards, `--radius-md` 6px controls, `--radius-sm` 4px badges).
- **Accent:** Indigo (`--accent`) for primary actions, active navigation, links, and the informational status. Never green-as-brand.
- **Texture:** Hairline borders are very subtle (`rgba(255,255,255,0.08)` in dark mode). Depth comes from soft shadows and surface steps, not heavy outlines.
- **Status rail retained:** The 3px left rail on problem table rows and dashboard cards (`--rail`) is kept as an information pattern — a degraded/critical host still "lights up" at a glance — reskinned to the new semantic colors.

## Color Mode Strategy

- **Dark mode is the default** (monitoring tool, dim environments — PM-004). Light mode is fully supported.
- Mode is set by a class on `<body>`: `.dark-mode` (default) or `.light-mode`. Every mockup and the design-system reference page includes a fixed, always-visible mode toggle (bottom-right) that swaps the class. The initial class defaults to the user's system preference but is always overridable in-app; the app must not rely on `prefers-color-scheme` alone.
- Both modes define a complete token set; no token is left to inherit across modes.

## Color Token Semantic Roles (PM-005)

Unified table — each token's role in BOTH modes (values differ per mode; roles are identical). See `design-system.css` for values.

| Token | Semantic role | Dark | Light |
|-------|---------------|------|-------|
| `--bg` | Page/body background | navy/slate-900 | slate-50 off-white |
| `--surface` | Panel / sidebar / table-body surface (one level up from page) | slate-800 | white |
| `--surface-2` | Elevated surface: table header, modal, row stripe | lighter slate | slate-100 |
| `--surface-hover` | Hover fill — **must be visually distinct from `--surface-2`** in both modes | lighter than surface-2 (slate-700) | darker than surface-2 (slate-200) |
| `--border` | Subtle hairline borders, dividers | `rgba(255,255,255,0.08)` | `rgba(15,23,42,0.10)` |
| `--border-strong` | Heavier dividers, header underline, table-head bottom | `rgba(255,255,255,0.16)` | `rgba(15,23,42,0.18)` |
| `--text` | Primary foreground | slate-100 | slate-900 |
| `--text-dim` | Secondary text, labels, dim columns | slate-400 | slate-600 |
| `--text-faint` | Tertiary, placeholders, eyebrows | slate-500 | slate-400 |
| `--accent` | Primary action, active nav, links | indigo-500 | indigo-600 |
| `--accent-dim` | Disabled/secondary accent, primary-hover | indigo-700 | indigo-300 |
| `--on-accent` | Text/icon on accent fills | white | white |
| `--focus` | Focus ring — **distinct from `--accent`** so focus is visible on accent elements | sky-400 | sky-600 |
| `--ok` / `--ok-bg` | Healthy status text / soft-tint background | emerald | emerald |
| `--warn` / `--warn-bg` | Warning/stale status text / soft-tint background | amber | amber |
| `--crit` / `--crit-bg` | Critical/offline/error status text / soft-tint background | red | red |
| `--info` / `--info-bg` | Informational/pending status, source tags, active-nav fill | indigo | indigo |

The accent (indigo) is the brand/action color; healthy/OK now uses its own emerald `--ok` token — accent and OK are no longer the same color. The `*-bg` tokens are low-alpha soft tints used as pill/badge/banner fills (consumer-tech "subtle background chip" style), not saturated blocks.

Hover-state verification: `--surface-hover` is lighter than `--surface-2` in dark mode and darker than `--surface-2` in light mode — distinguishable in both.

## Typography Usage Rules

- **System sans (`--font-sans`) is the default for everything** — headings, body, tables, buttons, inputs, help text. Stack: `-apple-system, BlinkMacSystemFont, "Inter", "Segoe UI", Roboto, sans-serif`. This gives the friendly consumer-tech feel. (Roles/values in `design-system.css`.)
- **Monospace (`--font-mono`) is reserved for technical fact values** — IP addresses, MAC addresses, UUIDs, fingerprints/hashes, cert serials, port numbers, raw IDs, durations, and code. Applied per element via `class="mono"` (table cells, `.kv` values) or via the `.tag` class. Stack: `"JetBrains Mono", "Fira Code", ui-monospace, …`.
- **Decision rule for mono vs sans in data:** identifier/technical value → mono; human-readable label/name (hostname, vendor, OS name, status label, credential reference) → sans. The primary name column reads as a label (sans), not a code.
- Scale: `--fs-2xl` page titles / big dashboard numbers; `--fs-xl` section headings + identity titles; `--fs-lg` subsection; `--fs-md` emphasis; `--fs-base` body; `--fs-sm` table cells; `--fs-xs` labels, eyebrows, tags, captions.
- Uppercase + `--tracking-caps` is reserved for eyebrows, group labels, and field labels — used sparingly; not for body, data, or table headers carrying long names.

## Spacing & Layout Rhythm

- 4px base scale (`--space-1`..`--space-8`). Dense by default: tables and toolbars use `--space-2`/`--space-3`; page chrome uses `--space-5`/`--space-6`.
- **Density tiers:** reporting tables and Device Detail are **dense**; Admin forms (Agent Detail, Credentials, Promotion modal) are **moderate** (more field spacing, help text) — a deliberate exception justified by lower frequency / higher consequence; the Login/Bootstrap screens are **spacious** (single centered card). These tiers are intentional, not drift.
- App shell: fixed left sidebar (`--sidebar-w`) + scrolling `.main`. Below the 960px breakpoint the sidebar becomes an off-canvas drawer (see `responsive.md`).

## Icon / Glyph Style

- Status uses **shape glyphs** carrying meaning independent of color: rounded square = ok, triangle = warn, circle = crit (see `.status` in `design-system.css`). This satisfies "no reliance on color alone" even with the softer consumer-tech palette.
- Status pills render as **soft-tinted chips** (low-alpha `*-bg` fill + colored text + shape glyph), consistent with consumer-tech badge styling, rather than outlined boxes.
- Sort indicators are `▲`/`▼` on the active column header.
- Icons are simple Unicode symbols or inline SVG at a consistent 16px size — no icon-font dependency. The aesthetic stays clean and uncluttered, not icon-heavy.

## Named Interaction Patterns (PM-006)

Every repeated interaction class has one canonical rule. Deviations must be documented as explicit exceptions (below).

- **navigation-pattern:** Role-aware persistent left sidebar, flat (Dashboard, All Hosts, Services, then hub links: Inventory, Network, and — admin only — Fleet, System, Data). Active item shows a rounded accent-tint pill (`--info-bg` fill + accent text, medium weight) — Linear/Raycast style — rather than a left-border bar. Fleet/System/Data are omitted entirely for the Read-Only Viewer. A hub link lands on its first member page, which renders a `_HubTabs` bar (`nav.tabs`/`a.tab`/`tab active`, the same shape Terrain's DHCP/DNS sub-views already used) linking to its sibling member pages — each still its own URL with its own filters/pagination, so only the *entry point* consolidated, not the underlying reports (see `information-arch.md` for the full rationale and hub membership). Drill-down navigates from a table row to an entity detail page; back returns to the originating list with its URL filter state intact.
- **create-pattern:** Self-contained records (targets, credentials) are created via a **modal form** launched from a page/section action button; explicit submit; success → toast. *Exception:* agent-level configuration is edited in a dedicated detail-page section with an explicit Save (not a modal) because it is a coherent multi-field config committed as a set — documented exception.
- **edit-pattern:** Same surface as create (modal for records; detail-section + Save for agent config). Secret fields are **write-only** — never pre-filled, never displayed. Credentials split this into two distinct actions rather than one combined edit: **Edit** changes only name/type and has no secret field at all; **Rotate** is the only action that changes the stored secret, via its own modal with a single new-secret field. Keeping them separate (not one form with an optional blank secret field) avoids an operator believing they changed a secret when they only renamed the credential.
- **delete-pattern:** Confirmation dialog. If the item is referenced by others (e.g., a credential used by targets), the dialog lists the dependents and requires explicit confirmation before proceeding.
- **confirmation-pattern:** Reserved for destructive, irreversible, or high-consequence actions only — delete (any), credential rotation of a referenced credential, and device promotion (final-step consequence restatement). Everyday low-consequence edits (rename, change interval, toggle a collector) get **no** confirmation, to avoid alert fatigue. High-consequence confirmations restate the consequence in plain language before the commit.
- **empty-state-pattern:** Tables/lists with no matching data show a centered `.empty` block with a one-line explanation and a recovery action (usually "Clear filters" or, for truly-no-data, a "create your first X" CTA). *Exception:* entity Detail pages (Device, Service) **hide** sections that have no data entirely rather than showing an empty state (per REQ-012) — an empty Containers/Battery tab on every host would be noise — documented exception.
- **selection-pattern:** No multi-row bulk selection in this iteration — there is no bulk operation in the requirements (promotion is per-device; deletes are per-item). Row click = drill to detail; inline action buttons (Promote) act on that single row. If bulk actions arrive later, add a leading checkbox column and a bulk-action bar; not built now.
- **filter-pattern:** Toolbar filters above the table (selects + free-text search), with **filter state encoded in the URL query string** (`?status=`, `?source=`, `?vendor=`, `?q=`). The URL is the source of truth; state is restored on load and updated via the router on change (replace, not push, for filter/sort changes). Dashboard summary cards deep-link to the matching pre-filtered URL (see `information-arch.md` for the card→URL map).
- **pagination-pattern:** Keyset/cursor pagination only (never offset), 100 rows/page default, Next/Previous controls carrying an opaque `?after=` cursor. Required by project DB guidance and REQ-037.
- **provenance-pattern (domain-specific):** For discovered devices, key identity fields show the canonical value (precedence `agent-direct > lldp > mdns > nbns > arp/dhcp`) with a "show all sources" toggle that reveals lower-precedence conflicting values with source attribution. Never silently drop a lower-precedence value. The Device Detail Discovery Sources panel shows the per-source first/last-seen and observation counts.
- **feedback-pattern:** Transient success → `.toast` (auto-dismiss). Persistent state/error → `.banner` (`.stale` warning, `.error` critical, `.info` neutral) inline at the affected scope. Per-section query failure shows an `.error` banner on that section only while other sections render. Whole-page failure routes to the clean error page (no stack traces / DB details ever — REQ-028).

## Layout Patterns for Analogous Screens

- **Reporting tables** (Device List, All Hosts, Storage, Ports, Containers, Hardware, Interfaces, Components, Subnets, ARP, Terrain/DHCP/DNS, Service List, Agent List, Credentials, Users, Conflicts, OUI Database, Activity Log): page-head (title + meta + refresh) → hub tab bar where the page is a hub member (see navigation-pattern) → toolbar (filters + search) → `table.data` with sticky header, sortable columns, status pills, row status-rails for problem rows, keyset pagination. All peer reporting screens share this shape. Change Feed keeps this same table shape but is no longer nav-linked (see `information-arch.md`); Sessions/Accounts/Security/Certs/Patches were never built. **Service List** keeps the full filter/pagination chrome despite low today's instance counts (only two service types exist) — a fleet could run one DNS/Home-Assistant pair per zone, so the grid earns its keep rather than degrading to a bespoke card list. Its type-specific columns (CA Status/CA Expiry/DNS Queries) were replaced with one **Highlights** column carrying a per-type one-line summary (DNS query/blocked stats, or Home Assistant device/area counts), so a row is never mostly dashes just because it's the "other" service type.
- **Entity detail** (Device Detail, Service Detail): persistent `.identity` header → section navigation → `.tabpanel` per section; single-instance sections use `.kv`, multi-row sections use `table.data`. Only sections that have data are rendered (documented empty-state exception) — **Service Detail's** CA/DNS/DHCP/Details tabs are each conditionally rendered based on whether that service instance actually has that kind of data, and the default active tab is the first one that does (not hardcoded), so a non-DNS service (e.g. Home Assistant) no longer lands on a dead "no CA data" panel. **Device Detail** uses a **grouped vertical section rail** (`.detail-body` grid → `.section-nav` with `.section-nav-group`/`.section-nav-item`, reusing the global nav's eyebrow-label + accent-pill language, multi-row items carrying a count chip) because it has ~40 sections — the built-in sections plus one destination per fact view (`FactViewLibrary.All`, grouped by `FactViewGroup`); a wrapping horizontal tab bar does not scale to that count. **Service Detail** keeps the horizontal `.tabs` shape (far fewer sections). The divergence is intentional and justified by section count; both still share the header + `.tabpanel` + `.kv`/`table.data` conventions.
- **Admin forms** (Agent Detail config, Credentials modal, Promotion modal): `.field` rows with labels + help, moderate spacing, explicit Save/Confirm, inline validation, no-partial-commit on error.
- **Composite dashboard** (Fleet Dashboard, SCR-003): a single-purpose page composed of stacked, grouped panels ordered **attention-first** (see `information-arch.md`). It does NOT use the reporting-table shape. Structure: `.dash-section` blocks (each an optional `.dash-eyebrow` group label + one or more `.dash-panel` containers). Multi-panel rows use `.dash-cols` (2-up) or `.dash-cols-3` (3-up), both auto-reflowing at narrow widths (no bespoke breakpoint). Each panel refreshes independently via its own htmx `?fragment=` target.

## Dashboard panel & viz components (SCR-003)

First-of-kind component classes introduced by the Fleet Dashboard redesign. They live in the mockup's local `<style>` block and are **candidate additions to `design-system.css`** (reimplement in Razor with these exact names). They use **only existing tokens — no new color tokens.** Peer mockups already carry local component classes this way (`.section-head`, `.kv-2col` in `device-detail.html`); this follows that established practice.

- **`.dash-panel`** — section container: `--surface` fill, hairline border, `--radius-lg`, `--shadow-sm`, `--space-5` padding. `.dash-panel.hero` adds a `--rail` `--accent` left rail for the top "Needs attention" panel. `.panel-head` is its title row (`h2` at `--fs-lg` + a right-aligned `.panel-action` drill-in link, `--border` underline).
- **`.attn-list` / `.attn-item`** — the Needs-Attention rows. Each `.attn-item` is a deep-linking `<a>` (grid: severity chip · count · label · `→`) with a `--rail` left rail colored by `.sev-crit` / `.sev-warn`. Severity is carried by **three redundant signals** (rail color + `.status` shape-glyph chip + the words "Critical"/"Warning" + the colored count), satisfying "no color alone." Zero-count concerns are omitted, not greyed.
- **`.stat` / `.stat-row`** — compact totals tiles for the Network section. Deliberately **smaller** than `.card` (value at `--fs-lg`, not `--fs-2xl`) so vanity totals stay visually subordinate to the hero attention counts. `.stat.warn` colors the value `--warn`.
- **Part-to-whole headline tiles (`.pw-bar` + `.pw-cap`)** — the Network totals tiles (Total devices, Services, Reporting-24h) share a part-to-whole motif: a thin segmented `.pw-bar` under the value + a `.pw-cap` caption naming each part with a matching swatch. **Colour is reinforcement only** — numbers, labels, and the caption carry the meaning; `.pw-bar` and swatches are `aria-hidden` decoration; caption parts may be links to the pre-filtered report. **Segment colour follows design-system semantics, not decoration:**
  - *Neutral category splits* (managed/discovered, service types) use the indigo family: `.seg-1`/`.sw-1` = `--accent`, `.seg-2`/`.sw-2` = `--accent-dim`, `.seg-3`/`.sw-3` = `--text-faint` ("other" tail). This echoes the `badge-managed` (solid accent) / `badge-discovered` (dim) distinction and never uses a semantic status colour for a non-status category.
  - *Health ratios* (reporting vs quiet) use the status tokens: `.seg-ok`/`.sw-ok` = `--ok`, `.seg-quiet`/`.sw-quiet` = `--warn` — consistent with "reporting = healthy, quiet = needs-attention" and the `--warn` used on the "Not seen recently" list.
  A tile that is a plain cardinality (Zones) uses no bar. Uses only existing tokens; no new palette.
- **RBAC-hidden markers in mockups (`data-role="admin"`)** — dashboard mockups annotate admin-only targets (the fingerprint-conflicts attention row, the Agents header action) with `data-role="admin"` so a reviewer can see what the Read-Only Viewer would NOT see. This is a **mockup annotation only** — in Razor these elements are omitted server-side for the viewer (they are never rendered, not merely hidden), consistent with the Admin nav group being omitted entirely (REQ-002). Peer mockups previously marked the single admin-only Promote button with a comment; `data-role` is the generalized, greppable form of that same convention.
- **`.act-list` / `.act-item`** — recent-activity list rows (main text + right-aligned `.act-when` timestamp, `--border` dividers).
- **Composition bars** — reuse the existing `.bar` primitive (`.bar` / `.bar.warn` / `.bar.crit`) inside a `.compo` grid (label · bar · count). The only override is `.compo .bar { width:100%; display:block }` so the fixed-80px meter grows to fill the row. No new bar styling. Group headers use the existing `.section-title`.
- **`.spark`** — **inline-SVG sparkline** (new lightweight-viz primitive; no charting library, per the project's minimal-dependency stance). A `<svg class="spark" viewBox="0 0 100 36" preserveAspectRatio="none" role="img" aria-label="…">` containing a `<polyline>` (stroke `--accent`, `vector-effect:non-scaling-stroke`) and an optional `.area` fill (`color-mix` of the accent). `.spark.warn` restyles it to `--warn` for the collection-error series. **Accessibility:** every sparkline MUST carry a descriptive `aria-label` summarizing the trend and current value (color/shape is not the only signal), since the trend line is otherwise inaccessible. Reserved for genuinely trend-shaped data (error rate over cycles, changes over days); do not use where a single `.bar` or a number suffices.
- **Status semantics unchanged** — online/valid → `.status.ok`; stale/degraded/warning → `.status.warn`; offline/failing/critical → `.status.crit`; pending/informational → `.status.info`. The agent mini-list reuses `table.data` with `tr.row-crit`/`tr.row-warn` rails exactly as the reporting tables do.

## Status & State Color Rules

- Status meaning maps to tokens consistently everywhere: healthy/online/valid → `--ok`; warning/stale/expiring/degraded → `--warn`; critical/offline/expired/failing → `--crit`; pending/informational → `--info`. The same word always uses the same color across screens.
- Every status indicator pairs color with a shape glyph and a text label.
- Management status: `badge-managed` (solid accent fill) vs `badge-discovered` (dashed outline) — the fill/outline difference carries meaning independent of color.
