---
agent: sdev-02a-ux-designer-phase1a
date: 2026-04-27
status: draft
revision_id: 1
---

# Usability Design — JMW.Agent

## UX Depth Level

`standard` — single persona, well-understood network-monitoring domain, no per-feature deep dives required.

## Domain Complexity

`standard` — network monitoring + IoT discovery is a mature product category. Boss is a software engineer; glossary terms (subnet, mDNS, OUI, MAC, observer, threshold) are part of his daily vocabulary and need no novice-affordance overlay.

## Discovery Inputs

`planning/ux/discovery.md` does not exist (Phase 0 was skipped per orchestrator guidance — single-persona, single-stakeholder home-lab project where the persona owner is the same person making product decisions). All design decisions in this artifact are grounded in:

- `PERSONA-001` — explicit profile, goals, pain points, usage patterns
- The MVP-vs-full-vision split in `requirements/index.md`
- Acceptance criteria of the UI-bearing REQs (REQ-014/015/016/017/018/019/039/052 and the auth/setup REQs REQ-004/005/006/007/008)
- Boss's already-stated UX preferences relayed by the orchestrator (dark default, plain HTML+vanilla JS+Go templates, dense info, late-night usage)

These inputs are treated as direct evidence: the persona owner has already specified preferences and constraints in the orchestrator conversation. Items inferred (rather than stated) are flagged as **Assumption** in the relevant section and listed in the Open Assumptions section at the end for the critic and Boss to confirm or override.

## User Mental Model Summary

Boss's mental model of his network, synthesized from PERSONA-001 + glossary + DEC-001:

**Top-level hierarchy:**

- The **network** is a set of **subnets** (often VLANs: server VLAN, IoT VLAN, guest VLAN, etc.).
- Each subnet contains **devices**.
- Devices come in two flavors that he keeps mentally distinct:
  - **Agent-backed devices** — his own Linux/macOS/Pi hosts running the JMW.Agent binary. Rich data: CPU, memory, disks, interfaces, Docker, services, reboot history, SMART.
  - **Discovered (agentless) devices** — printers, Chromecasts, Google Home, IoT, switches, APs, phones, anything that can't or shouldn't run an agent. Thin data: MAC, IP, mDNS service set, OUI vendor, which agents have observed it.
- A single physical device is one logical record even when multiple agents observe it (server-side dedup by MAC).

**The four questions Boss actually asks the dashboard:**

1. **Status check** — "Is everything okay?" (glance, daily, often from his phone).
2. **Triage** — "What just broke?" (reactive, after a Discord/Pushover ping; usually after-hours).
3. **Inventory awareness** — "What just appeared on my network?" (when a new device joins; when reviewing the IoT VLAN).
4. **Trend / planning** — "Why did this server reboot?" / "When did latency to Pi-hole start spiking?" / "Is this disk filling fast enough to matter?" (occasional, deliberate).

**Key implication for design:** The dashboard's primary mode is **scanning**, not **editing**. The interface needs to make "everything looks fine" answerable in under 2 seconds and "what's wrong" answerable in under 15. Editing (tags, thresholds, channels, settings) is rare and can afford friction.

**Late-night usage** — REQ-019 + Boss's stated preference: dark mode at 11pm. The interface will be used tired, in low ambient light, often on a phone. This argues for high contrast on status, generous tap targets despite the dense-table preference, and avoiding modal stacks that are hard to dismiss on touch.

## Cross-Cutting Decisions

These decisions apply to the whole product and are referenced by per-screen sections below.

### CC-1. Navigation: Persistent left sidebar, collapsible

**Decision:** A persistent left sidebar lists the top-level views as a vertical nav. The sidebar is collapsible to icon-only on narrow viewports (≤900 px, including phones in landscape) and replaced with a hamburger-revealed drawer on portrait phones (≤600 px). The active section is highlighted; route is shown in the URL and reflected in the breadcrumb at the top of the content area.

**Sections (top-level nav order):**

1. Dashboard (home)
2. Clients (registered agents + discovered devices once REQ-034 ships)
3. Discovery (three views per DEC-001 — see screen spec)
4. Topology (REQ-039)
5. Alerts (active + rules)
6. Events (event log)
7. Pending Agents (registration queue with badge count)
8. Tags & Groups
9. Settings (TLS, retention, scan cadence, PSK, notifications, backups)

**Rationale:**

- ~9 top-level sections is too many for a horizontal top nav without crowding or an "More ▾" overflow that hides destinations.
- Sidebar matches the mental model of a tool dashboard rather than a marketing site. Boss uses similar layouts daily (Grafana, GitHub, VS Code activity bar).
- Pending Agents needing a count badge ("3 waiting") is awkward in a top nav; in a sidebar it sits naturally next to the label.
- Sidebar can accommodate future sections (Backups view, Notification log) without redesign.

**Rejected alternatives:**

- *Top nav with dropdowns* — hides destinations behind hover/click; bad for keyboard, bad on mobile, makes the badge count for Pending Agents unviewable until the menu opens.
- *Command palette only (Ctrl+K everywhere)* — power-user delight but breaks the glance-and-scan goal; nothing is visible without typing. Boss's primary mode is glancing, not typing destinations.

**Power-user supplement:** A `Ctrl+K` / `Cmd+K` global command palette is added on top of the sidebar (not as a replacement) for jump-to-device-by-hostname, jump-to-tag, jump-to-section. This is a Should-Have (post-MVP) — sidebar alone is sufficient for MVP.

### CC-2. Information density: Dense by default, with deliberate breathing room on scan-first surfaces

**Decision:** Default to dense, table-first layouts (tabular rows, ~32–36 px row height, multiple columns visible without horizontal scroll on 1366×768). The Dashboard landing page and the Topology map are the two exceptions — those use spacious layouts because their job is at-a-glance, not at-density.

**Per-surface density:**

| Surface | Density | Rationale |
|---|---|---|
| Dashboard landing | Spacious | Glance. Big readable numbers in summary cards; recent-activity feed in moderate density. |
| Client list | Dense table | Boss is technical; he wants 50 hostnames visible at once, sortable, filterable. |
| Discovered devices | Dense table | Same reasoning. |
| Device detail | Mixed | Header is spacious (identity, status, big metrics). Scroll-down sections (events, services, containers, history) are dense. |
| Alerts list / rules | Dense table | He'll scan many rules. |
| Event log | Dense table | High-volume; filter+scan is the only way to use it. |
| Pending agents | Dense table | Approve/reject inline, multiple at a time. |
| Notification channels / Settings | Form, comfortable spacing | Edit-rare, read-rare. Errors here have consequences (paged at 2am because email is misconfigured) — favor legibility over density. |
| Tags & Groups | Dense list | Many entries, simple values. |
| Topology | Spacious / spatial | A graph by definition. |

**Rationale:** PERSONA-001 explicitly contrasts JMW.Agent against "heavy, opinionated" tools. Dense tables are the convention in tools Boss already lives in (htop, journalctl, pgAdmin, kubectl get pods, Netdata's older UI). Card-grid alternatives waste screen real estate at his fleet size (75 devices) and force horizontal scanning across cards instead of vertical scanning down a column — a worse fit for "is anything red?"

**Mobile consequence:** On phones, dense tables collapse into a single-card-per-row layout (essentially a one-column table) with the most important columns (status dot, hostname, last-seen) shown by default and a tap to expand the rest. We do **not** maintain two separate table designs — the same template responsively reflows.

### CC-3. Real-time refresh: Polling, no WebSockets

**Decision:** All "live" surfaces use HTTP polling. Polling is implemented either with htmx `hx-trigger="every 15s"` on the partial template, or vanilla `setInterval` calling `fetch()` and patching innerHTML. No SSE, no WebSockets, no long-polling for MVP.

**Polling cadences:**

| Surface | Cadence | Notes |
|---|---|---|
| Dashboard summary cards | 15 s | Within REQ-017 #3's ≤30 s budget; chosen tighter so phone-glances feel live. |
| Client list status badges | 15 s | Within REQ-014 #2's ≤30 s budget. |
| Device detail live metrics | 15 s | Within REQ-015 #3's ≤30 s budget. |
| Event log "tail" | 10 s | The tail of the page only — older entries are paginated. |
| Pending agents queue | 30 s | Acceptable latency for approval workflow. |
| Topology map | manual refresh button + auto on tab focus | REQ-039 #5: near-live not required. |
| Settings / Tags / Channels | none (page load only) | Edit-rare. |

**Tab-visibility behavior:** When the page is hidden (`document.visibilityState === 'hidden'`), polling pauses to save battery on phones and load on the server; it resumes immediately on focus and triggers a one-shot fetch on resume.

**Rationale:** Polling is sufficient for the ≤30 s live-indicator budget in `quality-standards.md` and avoids the operational complexity of WebSockets (sticky sessions, reconnect logic, edge proxies). A single Go server, a SQLite backend, and 75 agents at 15 s polling is well under any meaningful load.

**Future option flagged:** SSE (Server-Sent Events) is a clean upgrade path if real-time becomes a requirement (e.g., for an alert-fired flash). Polling does not block this — partial templates are the same shape either way.

### CC-4. Destructive-action confirmation pattern matrix

Three confirmation tiers, applied consistently across the product:

| Tier | Action examples | Pattern |
|---|---|---|
| **Tier A — Routine, undoable** | Add/remove a tag; mark device ignored; rename a device; restore an ignored device; rotate a notification channel's enabled state. | **No confirmation; immediate action; undo toast** for 8 s in the bottom-left. The toast lists the action and offers "Undo." Closing the toast leaves the action committed. |
| **Tier B — Consequential, recoverable** | Deregister an agent (historical metrics retained per REQ-022; agent must re-register); reject a pending agent; delete a tag/group definition (not the devices); disable an alert rule. | **Modal confirmation** with the action verb on the primary button and the device/object name in the modal title and button (e.g., "Deregister `jmw-pi-01`"). Cancel button is the modal's default focus. No type-to-confirm. |
| **Tier C — Permanent, irreversible** | Purge an archived discovered device (deletes the record + all observations); revoke and re-issue the pre-shared key (invalidates pending agents); delete the admin user during password recovery; manually delete the SQLite DB / "factory reset". | **Modal confirmation with type-to-confirm.** User must type the device hostname / `RESET` / `REVOKE` exactly into a text field before the destructive button is enabled. Modal explicitly enumerates what will be lost. |

**Rationale:** Type-to-confirm is friction for a single user; using it everywhere would train Boss to ignore it. Reserving it for permanent data loss preserves its signal value. Toasts handle high-frequency edits (tagging) without slowing the workflow. Modals on Tier B prevent fat-finger deregistration without becoming a wall of clicks.

**Late-night safety net:** The Tier C modal additionally shows the time of day in its banner ("It's currently 02:14 — are you sure?") when invoked between 22:00 and 06:00. Speed-bump for tired-Boss; cheap to implement; defensible against the persona's stated late-night usage.

### CC-5. Empty / first-run experience

**Decision:** Two distinct empty-state experiences:

- **Bootstrap (no admin)** — REQ-004 already specifies an opinionated single-page setup wizard. Three fields: admin username, password, optional pre-shared key. One submit. No back-navigation, no progress bar, no "Welcome to JMW.Agent" preamble. Submit lands the user directly on the Dashboard.

- **Post-bootstrap, zero agents registered** — drop the user into the Dashboard with a populated layout and zero-state messaging in the cards ("0 devices online — install your first agent"). A persistent **"Install an agent"** call-to-action card sits in the activity-feed slot until at least one agent is registered, with three tabs inside it: `Linux (apt/yum)`, `Linux (binary)`, `macOS`, each showing a copy-paste install command and the server's TLS fingerprint pre-filled. The same content is also linked from the sidebar Pending Agents view ("No pending agents — see install instructions").

**Rationale:** Boss explicitly does not want a guided tour (PERSONA-001: pain points include "heavy, opinionated" tools). The empty state is functional, not pedagogical: the layout he'll see for the rest of the product's life is the layout he sees on day one, with the missing data shown honestly and the one piece of friction (installing the first agent) removed by giving him the exact command.

**Other empty states across the product** — every list view (Clients, Discovery, Alerts, Events, Pending Agents, Tags) has a single-line zero-state ("No matches" / "No alerts firing — nice." / "No events in the selected range") rather than illustrated empty-state graphics. Tone is dry, not chirpy.

### CC-6. Error / failure / loading patterns

- **Loading skeleton** for full-page first paint (gray placeholder rows in tables, gray rectangles in cards). Polling refreshes do **not** show a skeleton — they update in place silently or show a small spinner in the section header if the request takes >1 s.
- **Form validation** is inline, on blur, with a red-bordered field and a short error message below the field. Server-side validation errors render the same way after submit.
- **Async action result** (e.g., "Force a heartbeat") shows a success toast on completion or a red error toast on failure with the server's error message verbatim. Failures do not roll back the user's draft state.
- **Connection lost** — if a polling request fails three times consecutively, a yellow banner appears at the top of the page: "Lost connection to server — retrying." The banner clears on the next successful poll. The page does not freeze; cached data remains visible.
- **Stale data marker** — when polling is paused (tab hidden) or has failed, a small "Last updated 2m ago" timestamp under the section header reveals the staleness; otherwise it's hidden.

**Rationale:** Boss's mental model of a tool dashboard is that it never blocks him. The loading-skeleton pattern is for first paint only; refreshes need to be invisible or near-invisible. Connection-lost banner respects the persona's late-night reactive use — a frozen page at 02:00 is worse than a stale page that admits it's stale.

### CC-7. Status semantics — green / yellow / red are reserved

Across the product, color-status semantics are fixed:

| Color | Meaning | Used on |
|---|---|---|
| Green | Healthy / online / OK | Status dots, summary card "online" tile, alert state "resolved." |
| Yellow | Warning / degraded / pending | Pending agent registration; agent reporting but with a warning threshold tripped; cert within 30 days of expiry. |
| Red | Offline / firing / error | Offline agent; firing alert; failed notification dispatch; cert expired. |
| Gray | Unknown / archived / deregistered / ignored | Deregistered agent in the Archived view; ignored discovered device; class "unknown" before classification ships. |

**Accessibility:** Per `quality-standards.md` (WCAG 2.1 AA), color is **never the only signal**. Every status dot carries a redundant text label or icon (✓ / ⚠ / ✕ / —) and an `aria-label`. Color-blind-safe palette is verified in Phase 3 mockups.

### CC-8. Keyboard / power-user affordances

Boss is a software engineer who lives at the keyboard. The product earns trust by being keyboard-first.

**MVP:**

- All primary actions reachable via `Tab` order in a sensible reading order; focus rings visible (not removed in CSS).
- `/` focuses the search box on the current page.
- `Esc` closes any open modal or drawer; loses no draft data without confirming.
- Tables: arrow keys navigate rows, `Enter` opens detail view.
- Form submit: `Cmd/Ctrl+Enter` from any input submits the form.

**Should-Have (post-MVP):**

- Global command palette `Cmd/Ctrl+K`.
- Keyboard shortcut help modal `?`.

**Rationale:** No novice-affordance counter-pull here — single persona, that persona is the keyboard user. A help-overlay key (`?`) is the only "novice" affordance and it's also useful for the expert who forgot a shortcut.

### CC-9. Progressive disclosure: MVP-vs-full-vision rule

REQs 014 and 015 already specify "stable layout — sections that depend on unshipped REQs are simply absent rather than empty placeholders." This is generalized as a product-wide rule:

- **Sections, columns, and cards** that depend on unshipped should-have REQs are **omitted** from the rendered DOM in MVP, not shown as "Coming soon" placeholders.
- **Layout containers** (the parent grid / column widths / sidebar item position) **remain fixed**, so when a feature ships the new section appears in its predetermined slot without shifting other content.
- For features with feature flags (e.g., REQ-034 discovery), the flag controls section presence; when the flag flips, no client-side layout shift.

**Rationale:** Boss is the implementer of his own future. Empty-coming-soon placeholders feel like marketing residue in a tool he owns. Stable layout means his muscle memory survives the upgrade.

## Screen / Task Decisions

### S-1. First-boot setup wizard (REQ-004)

- **Primary user goal:** Bootstrap the system. One-time, ever.
- **Task frequency:** Once per installation.
- **Component choices:**
  - **Single-page form**, not a wizard — *rationale:* three fields don't justify multi-step. A single submit button is faster and lower-error than Next/Next/Submit.
  - **Username** + **Password** + **Confirm password** + **optional Pre-Shared Key** + **Generate PSK** button (fills a strong random 32-byte hex). Plus a **Save PSK file** button that downloads a `psk.txt` for safekeeping.
  - PSK field shows the generated value in plain text (this is bootstrap on a freshly installed server; nobody is shoulder-surfing). A "Hide" toggle is available.
  - Password meets a stated minimum (length 12+, no max); strength meter shown inline, not gating.
- **Information density:** Spacious. This is a one-time gate; legibility wins.
- **Progressive disclosure:** PSK and "Generate PSK" sit in a collapsed section "Auto-approve agents (optional)" that's open by default — Boss will use it, but a user who's never heard of PSK can ignore it.
- **Process shape:** Single page. After submit the user is redirected to `/login` (not auto-logged-in — this proves the credentials work and exercises the login path on day one).

### S-2. Login screen (REQ-005)

- **Primary user goal:** Sign in.
- **Task frequency:** Daily-ish, and after long idle.
- **Component choices:**
  - Centered card on a near-black background; product name + lockup; username field; password field; "Sign in" button. No "Remember me" toggle (sessions already persist per REQ-005 #4). No "Forgot password?" link — recovery is CLI per REQ-006, and surfacing a UI link sets a wrong expectation.
- **Information density:** Spacious / minimal. Single focal point.
- **Progressive disclosure:** N/A.
- **Error recovery:** Failed login shows a generic "Invalid username or password" inline (not under each field, to avoid user-enumeration via differential errors). Rate-limit lockouts (REQ-005 #5) show a separate message with a wait-time hint.

### S-3. Dashboard / overview (REQ-017)

- **Primary user goal:** Status check — answer "is everything okay?" in <2 s.
- **Task frequency:** Many times per day, plus reactive triage triggered by notifications.
- **Component choices:**
  - **Top: row of summary cards.** Cards are flex-wrapped at narrow viewports. Each card is a clickable link to the relevant filtered list:
    - Devices online / offline / pending (links to client list filtered by status)
    - Open alerts by severity (red count, yellow count) (links to alerts list filtered)
    - Total free disk (across the fleet, with a "click for breakdown" affordance to detail view of disk usage; in MVP it's a static aggregate sourced from REQ-011)
    - Devices reporting in the last hour (sanity check that ingestion is working)
    - Recent discovery (hidden until REQ-034 ships; layout slot reserved per CC-9)
  - **Below: two-column layout on desktop, stacked on mobile:**
    - Left: **Recent activity** — last 10 events from the event log (REQ-018), with link to full event log. Each row is a clickable line: timestamp + severity icon + short message + device link.
    - Right: **Active alerts** — currently firing alerts, condensed (severity + device + threshold). Each row links to the device detail view's alerts section. Empty state ("No alerts firing.") is the goal state.
  - **Polling:** 15 s for the cards; 10 s for the activity tail; 15 s for active alerts.
- **Information density:** Spacious for the cards; moderate for the two-column lower section.
- **Progressive disclosure:** Cards link out to filtered lists rather than showing per-card drill-downs in place. Keeps the dashboard a launchpad, not a detail view.
- **Process shape:** Hub. Dashboard is the destination after login and the home button (clicking the product logo) returns here.
- **Error recovery:** Per CC-6.

### S-4. Client list view (REQ-014)

- **Primary user goal:** Find a specific device, or scan all devices for problems.
- **Task frequency:** Several times per day.
- **Component choices:**
  - **Dense data table.** Columns (in MVP): status dot, hostname, IP(s), class, last seen, tags, group, kebab-menu actions.
  - **Sticky header row** so column labels remain visible during scroll.
  - **Top toolbar:** search input (placeholder "Search hostname, IP, or MAC"), status filter chips (`All / Online / Offline / Warning / Pending`), class filter chip, subnet filter chip, tag filter chip with multi-select, "+ Bulk action" dropdown enabled when ≥1 row selected.
  - **Row checkboxes** for multi-select; bulk operations: Add tag, Remove tag, Move to group, Deregister (Tier B confirmation).
  - **Sort:** clicking a header sorts; shift-click adds a secondary sort. Default sort is `status DESC, last_seen DESC` (offline devices first, then most recently active).
  - **Row click** opens the detail view (REQ-014 #5).
  - **Archived / Ignored filter** is a toggle in the toolbar (`Active | Archived | Ignored`) — three mutually exclusive views over the same table.
  - **MVP omissions per CC-9:** observer-count column, MAC-vendor column, sparkline-mini column (a single inline sparkline of the last 24h CPU) all reserve their column slot in the layout but are absent until their REQs ship.
- **Information density:** Dense.
- **Progressive disclosure:** Class / Subnet / Tag filters are inline chips, not a hidden "Advanced" panel — Boss will use all of them.
- **Process shape:** Linear browse-and-drill.
- **Error recovery:** Bulk action failures (e.g., one of 5 deregistrations fails server-side) show a partial-success toast: "Deregistered 4 of 5. `jmw-pi-03` failed: <error>." Retained selection and surfaces the failure for retry.

### S-5. Client / device detail view (REQ-015)

- **Primary user goal:** Investigate a specific device — current state, history, configured alerts, lifecycle action.
- **Task frequency:** Several times per day during triage; otherwise occasional.
- **Component choices:**
  - **Header band** (spacious): hostname (large, editable inline by clicking), status dot + state label, IP(s), MAC, OS, class, tags (chip row), group. Right side: action-bar with `Force heartbeat`, `Refresh discovery`, kebab-menu containing `Deregister` (Tier B), `Mark ignored` / `Restore` (Tier A toast-undo) for discovered devices, `Archive` / `Purge` (Tier A / Tier C respectively).
  - **Below the header: tabbed content** for sections that are heavy and don't all need to render at once:
    - `Overview` (default tab) — Live metrics tiles (CPU%, mem%, disk %, primary nic throughput); section for sparklines once REQ-021 ships; "Recent events for this device" condensed list (5 most recent).
    - `Metrics` — Full metric history with sparkline expansion, retention tier selector. Hidden in MVP if REQ-021 not shipped (tab itself absent per CC-9).
    - `Alerts` — Configured threshold rules for this device + currently firing alerts. Add/edit/disable rule actions.
    - `System` — OS info, uptime, reboot history (REQ-027 dependent), pending updates (REQ-033 dependent).
    - `Containers` — Docker list (REQ-031 dependent).
    - `Services` — Listening services (REQ-032 dependent).
    - `Storage` — Disk list with SMART (REQ-030 dependent).
    - `Network` — Interfaces, bandwidth (REQ-028 dependent).
    - `Discovery (observer)` — for agent-backed devices: what this agent has observed on its subnet. (REQ-034 dependent.)
    - `Discovered details` — for discovered-only devices: mDNS services, vendor (OUI lookup), observing agents list. (Replaces several of the agent-only tabs since REQ-015 #2 says only meaningful sections render.)
    - `Events` — full event log filtered to this device.
  - **Live-region** for live metrics tiles updates at 15 s (CC-3).
  - **Differentiation registered-agent vs. discovered-only** (REQ-015 #2): a small icon next to the hostname and a one-line subhead: `Registered agent — last heartbeat 2 min ago` vs `Discovered device — observed by 3 agents, last seen 5 min ago`. Tabs visible in each case differ per the list above.
- **Information density:** Mixed. Header spacious; tab content dense.
- **Progressive disclosure:** Heavy sections (metrics history, containers, full event log) live in tabs to keep first paint fast (REQ-015 #1's <1 s budget). Tab content lazy-loads on first activation; cached for the session.
- **Process shape:** Hub-and-spoke (the detail page is the hub, lifecycle actions branch out and return).
- **Error recovery:** Inline edit (hostname, tag) saves on blur; failure restores prior value and shows a red inline message under the field.
- **Deregistered banner** (REQ-015 lifecycle): a full-width yellow banner across the top of the header, reading "Deregistered on 2026-04-15. Detail view is read-only."

### S-6. Discovery views (DEC-001, REQ-034)

DEC-001 specifies three views over the same underlying data: *per-subnet*, *per-observer*, *unified*. These three views are exposed as **three sub-tabs within a single Discovery section** — same data, three lenses.

- **Primary user goal:** Inventory awareness (Q3 in the mental model).
- **Task frequency:** Occasional — when adding a device, or when reviewing what's on the IoT VLAN.
- **Component choices:**
  - **Top toolbar shared across all three sub-tabs:** search (hostname/IP/MAC/vendor), filter (status, class, last seen range, tags), `New since: 24h | 7d | All`, classify-and-promote action.
  - **Sub-tab `By subnet`:** dense table grouped by subnet (collapsible group headers, one row per device, observer-count column).
  - **Sub-tab `By observer`:** dense table grouped by observing agent (each agent's "what I've seen on my subnet" report).
  - **Sub-tab `Unified`:** dense flat table, default sort by `last_seen DESC`. The default landing sub-tab.
- **Information density:** Dense.
- **Progressive disclosure:** Three sub-tabs are the disclosure mechanism — same data, ranked by which question Boss is asking. Sub-tab choice persists across navigation in the session.
- **MVP note:** Whole section hidden by feature flag until REQ-034 ships; sidebar nav item also hidden. Layout slot reserved.

### S-7. Topology map (REQ-039)

- **Primary user goal:** Spatial situational awareness — see the network as a graph.
- **Task frequency:** Occasional. Demos / orientation / sanity check after subnet changes.
- **Component choices:**
  - **SVG-based force-directed graph** rendered server-side as the initial layout (computed once on page load from current data) and made pan/zoomable client-side via vanilla JS pointer-event handlers. No third-party graph library required for MVP — the data set (≤75 nodes, ≤200 edges) is well within hand-rolled SVG range.
  - **Subnets** are rendered as translucent rounded-rectangle containers behind the nodes, labeled at the top-left. **Agents** are larger nodes; **discovered devices** are smaller nodes; an **edge** runs from each discovered device to each observing agent.
  - **Pan**: click-and-drag empty space. **Zoom**: scroll wheel or pinch. **Reset**: a "Fit to view" button in the toolbar.
  - **Click a node** opens a right-side **drawer** (not a modal) with the same identity panel as the device detail view, plus a "Open detail view" link. Clicking another node updates the drawer; clicking the canvas closes it.
  - **Hover a node** shows a tooltip with hostname + status + last-seen.
  - **Toolbar:** filter by tag/group, filter by class, layout-mode toggle (Force-directed | Grid-by-subnet), "Refresh" button (manual, per REQ-039 #5), "Fit" button.
  - **Auto-refresh** on tab focus only; no polling on this view (graph re-layout would be visually disruptive).
- **Information density:** Spatial; conceptually spacious but visually packed.
- **Progressive disclosure:** Detail drawer is the disclosure mechanism. Node labels are abbreviated by default and become full on hover.
- **Process shape:** Free-form exploration.
- **MVP note:** Whole section hidden by feature flag until REQ-039 ships.
- **Performance note:** REQ-039 #3 requires graceful scaling to 75 nodes; a 75-node force layout is trivial, but as a guardrail we cap to 200 visible nodes via pagination warning ("250 devices match — showing top 200 by status. Add filters to narrow.") if the data ever exceeds.

### S-8. Alerts view (REQ-023)

Two sub-tabs:

- **`Firing` (default):** dense table of currently firing alerts. Columns: severity, device, rule name, threshold, fired-at, duration, actions (`Acknowledge`, `Silence for…`).
- **`Rules`:** dense table of all configured threshold rules. Columns: enabled, name, scope (global / tag / group / device), metric, condition, sustained-for, severity, channel(s), actions (`Edit`, `Disable`, `Delete`).

- **Primary user goal:** Triage (Q2) on Firing tab; configuration on Rules tab.
- **Component choices:**
  - **Add rule** button on Rules tab opens a side drawer (not a modal) with a form. Drawer chosen over modal because rule editing involves many fields and reference data (channels, devices, tags) — a side drawer keeps the rules table visible for context.
  - **Acknowledge** an alert is a Tier A action with undo toast. **Silence for…** opens a small popover with `15m / 1h / 4h / 12h / 24h / Custom` choices.
- **Information density:** Dense.
- **Progressive disclosure:** Rule scope (global vs. tag-scoped vs. per-device) is a single dropdown in the form, with a tag/group/device picker that appears when relevant. Avoids three different "create rule" flows.
- **Error recovery:** Saving a rule with no effect ("CPU > 100%" — impossible) returns a server-side validation error rendered inline under the offending field.

### S-9. Event log (REQ-018)

- **Primary user goal:** Trend / planning (Q4) and post-incident review.
- **Task frequency:** Occasional — but when used, used heavily.
- **Component choices:**
  - **Dense table with virtualized scrolling** (or paginated — see below). Columns: timestamp, severity, type, device, message, expand-for-details (kebab opens the full structured detail).
  - **Toolbar:** time-range picker (`Last hour / 24h / 7d / 30d / Custom`), severity filter chips, type filter (multi-select), device filter, free-text search across the message field, `Export…` button (CSV / JSON per REQ-018 #5).
  - **Pagination strategy:** server-side keyset pagination by `(timestamp DESC, id DESC)`. UI shows "Load older" button at the bottom of the table; "Load newer" appears at the top when the user has scrolled and new events have arrived above the current view. (Avoids virtualization complexity in plain JS.)
  - **Real-time tail:** a small toggle "Live tail" in the toolbar, off by default. When on, polls every 10 s and prepends new rows; when off, the view is a static snapshot (good for forensic review).
- **Information density:** Dense.
- **Error recovery:** Export failure shows toast + reason. Filter-no-match shows zero-state row.

### S-10. Notification channels (REQ-024)

- **Primary user goal:** Configure where alerts go.
- **Task frequency:** Rare — set once, edit when something changes.
- **Component choices:**
  - **List of channels** as a comfortably-spaced card list (one card per channel: type icon, name, target, enabled toggle, "Test" button, kebab for `Edit / Delete`). Card layout (not table) chosen here because edit is the primary verb and a card affords editing more naturally than a row.
  - **"+ Add channel"** opens a side drawer with a type selector (`Email / Discord / Pushover / Gotify`) and the type-specific fields (e.g., Discord webhook URL; Pushover user-key + app-token; Gotify URL + app-token; Email SMTP host/user/pass/from).
  - **Test button** sends a test notification immediately and shows result inline ("Sent ✓ at 14:32" or "Failed: <reason>").
  - **Quiet hours / dedup** (REQ-025/026) live in a separate "Notification Settings" section above the channel list, since they apply globally rather than per-channel.
- **Information density:** Comfortable spacing. Edit-rare; correctness-critical.
- **Error recovery:** Test failure displays the server's error verbatim (HTTP status, body excerpt) — Boss is technical and will troubleshoot from the actual error.

### S-11. Pending agents queue (REQ-007 / REQ-009)

- **Primary user goal:** Approve a new agent (or reject a stale one).
- **Task frequency:** Rare — when standing up a new server.
- **Component choices:**
  - **Dense table** with columns: registered-at, hostname, IP, OS, fingerprint, expires-at countdown, actions (`Approve`, `Reject`, `View details`).
  - **Approve** is one click; primary green button. **Reject** is one click; secondary button with a Tier B confirm modal (`Reject jmw-pi-03?`).
  - **Sidebar nav item** carries a count badge ("Pending Agents 2") — fixed by CC-1.
  - **REQ-009 expiry**: rows show a countdown ("expires in 14m"); on expiry the row gray-fades and re-categorizes under an "Expired" sub-tab.
  - **Inline fingerprint** column shows truncated `sha256:abcd…1234` with a click-to-copy and a "matches PSK?" indicator (✓/—) when a PSK is configured.
- **Information density:** Dense.
- **Progressive disclosure:** "View details" opens a drawer with full request payload (hostname, declared OS, declared MACs, fingerprint full) for forensic review of suspicious requests.

### S-12. Tags & Groups management

- **Primary user goal:** Maintain the tag/group taxonomy; rename, delete, see usage.
- **Task frequency:** Rare.
- **Component choices:**
  - **Two side-by-side panels** on desktop (Tags | Groups), stacked on narrow viewports. Each panel is a dense list: name, device count, actions (`Rename`, `Delete`).
  - Inline rename via click-to-edit (Tier A — undo toast). Delete is Tier B (confirm modal: "Delete tag `critical`? Removes from 12 devices.").
  - **Bulk tag/group editing happens on the Client list (REQ-016 #5)**, not here. This screen exists for taxonomy management.
  - **"+ Add tag" / "+ Add group"** is an inline empty input at the bottom of each list with a save button.
- **Information density:** Dense.

### S-13. Settings

A scrolled single-page-ish settings layout with anchored section headers in a left sidebar of its own (a sub-nav inside Settings). Sections:

- **Profile** — change admin password, idle-timeout setting.
- **TLS / Certificates** (REQ-052) — current cert fingerprint + SAN + expiry, "Upload custom cert" file picker, "Regenerate self-signed" button (Tier C confirm — agents must re-pin).
- **Agent registration** — PSK display (with reveal toggle and copy-to-clipboard), "Rotate PSK" button (Tier C confirm).
- **Heartbeats / scan cadence** (REQ-012 + DEC-001 discovery cadence) — number inputs with sane bounds, validation inline.
- **Retention** (REQ-022) — read-only display of the tiered policy in MVP; configurable knobs are post-MVP.
- **Notifications** — link out to the Notification channels screen (S-10).
- **Backups** (REQ-046 / REQ-047) — schedule + "Download backup now" button, last-snapshot status.
- **About** — server version, build, Go version, SQLite version, DB path, data dir size, link to docs.

- **Information density:** Comfortable form spacing per CC-2.
- **Component choices:** Forms with inline-edit + explicit Save button (not save-on-blur — Settings is the one place where saving on blur would be dangerous if a value is typo'd mid-edit).
- **Progressive disclosure:** Sections are collapsible; sub-nav shows section names; "Advanced" sub-section in some areas (e.g., TLS — "Show fingerprint history") for rarely-needed details.

## Open Assumptions for Critic / Boss Review

These are decisions made under "fully agent-driven" mode that may warrant Boss's confirmation before downstream work. None are blocking; default behavior is documented above.

1. **Sidebar nav order** (CC-1) — Dashboard, Clients, Discovery, Topology, Alerts, Events, Pending Agents, Tags & Groups, Settings. Alternative: collapse Discovery + Topology under a single "Network" group. *Default chosen because Boss may want Discovery and Topology surfaced as peers given how often he asked about each in the requirements.*
2. **Polling cadences** (CC-3) — 15 s for primary live surfaces (REQ budget is ≤30 s; we chose tighter). If server-load becomes a concern with 75 agents at 15 s, we can dial back to 30 s without UI changes.
3. **Tier A undo-toast duration** (CC-4) — 8 s. If Boss prefers persistent action history over ephemeral toasts, an alternative is a "Recent actions" tray in the bottom-right; deferred unless requested.
4. **Late-night Tier C banner** (CC-4) — assumes 22:00–06:00 in the server's local timezone. May be too cute; easy to remove if Boss objects.
5. **htmx vs. vanilla JS choice per surface** is left open. Both are acceptable per Boss's preferences; the IA / wireframe phase will decide per-surface based on partial-render boundaries. This phase commits only to "polling, no SPA framework, no WebSockets in MVP."
6. **Topology map: hand-rolled SVG vs. one third-party graph library** — DEC-006 (strict third-party dependency policy) prefers the former. We've defaulted to hand-rolled SVG. If forces-directed math becomes painful, a tiny library (`d3-force`) is the fallback; flagged for Phase 4 architecture review.
7. **Settings section "Retention is read-only in MVP"** — not derived from a REQ, but an inferred MVP scope-narrowing. Confirm with planner.

## Self-Review Checklist

- [x] Every screen/task area has explicit component choices with rationale.
- [x] Every cross-cutting decision has rejected-alternatives discussion.
- [x] Progressive disclosure decisions are explicit (CC-9 is the rule; per-screen disclosure is documented).
- [x] Error recovery covers destructive (CC-4 tiers), failure (CC-6), and lifecycle (S-5 deregister banner) cases.
- [x] Expert/novice strategy: documented as "single persona, expert; only novice-affordance is the `?` keyboard help and progressive disclosure on the bootstrap PSK section." (CC-8.)
- [x] Cross-screen consistency: status semantics (CC-7), confirmation tiers (CC-4), navigation (CC-1), polling pattern (CC-3) are all single sources of truth referenced by per-screen sections.
- [x] Decisions reference the persona's stated goals, pain points, and usage patterns rather than generic UX principles.
- [x] Domain complexity recorded in `index.md` (`standard`); matches the artifact header.
- [x] Open assumptions listed for the critic to scrutinize and Boss to override.
- [x] WCAG 2.1 AA color-not-sole-signal rule explicit (CC-7).
- [x] MVP-vs-full-vision stable-layout rule explicit (CC-9).
