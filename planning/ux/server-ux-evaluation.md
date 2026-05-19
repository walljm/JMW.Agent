---
agent: sdev-02a-ux-designer-onboarding
date: 2026-05-19
status: onboarding-inferred
scope: server web UI (templates + static)
---

# JMW Agent Server — UX & IA Evaluation

> Read-only assessment of the server-rendered admin UI as it stands. Source under [internal/server/http/templates](../../internal/server/http/templates), [internal/server/http/static/app.css](../../internal/server/http/static/app.css), [internal/server/http/static/app.js](../../internal/server/http/static/app.js), routing in [internal/server/http/server.go](../../internal/server/http/server.go).

## 1. Executive Summary

- **Information architecture is muddled around the "thing" model.** The top-level nav treats `Clients`, `Devices`, `Containers`, `Pending`, and `Terrain` as five peer concepts, but they aren't peers: `Pending` is a substate of `Clients`, `Containers` is a property of a host, `Terrain` is the upstream DNS/DHCP server (not a fleet thing at all), and `Devices` is the discovery view that overlaps with `Clients` (every approved agent is also a discovered device — see the "Managed client" callout on device detail). An operator has to hold an extra mental model the product does not need.
- **Terminology is inconsistent and partly off-brand.** The same entity is called *agent* (dashboard KPI, pending page copy, data model), *client* (nav, URLs, list page title), and *host* (containers page column). The page named **"Key Cyber Terrain"** is military jargon attached to a tool whose stated audience is one home-lab operator. Pick one term per concept and apply it everywhere.
- **The `/alerts` page conflates three concerns** — firings (an event feed), rules (config), and channels (config) — on a single flat page with two collapsed `<details>` forms. This is the highest-friction page in the product for a user trying to set up monitoring, and it also hard-codes 3 of N supported metrics and 2 of 4 promised channel kinds (REQ-024 names Discord, Pushover, Gotify; the UI offers only webhook + email).
- **Light theme is dead code, mobile is unconsidered, and several badges fail contrast in light mode.** `layout.html` hard-codes `data-theme="dark"`, there is no UI toggle, and badge state colors are hard-coded hex (`#2dd4a4`, `#fbbf24`, etc.) that do not adapt to the light palette. The navbar is a flex row of 8 links with no responsive collapse; data tables (10 columns on `/devices`, 8 on `/clients`) have no horizontal scroll wrapper. REQ-019 ("dark-mode-default responsive UI") is half-met.
- **A handful of small bugs are visible without running the server**: auth page `<title>` is missing its separator (`{{.Title}}JMW Agent` → renders "Sign inJMW Agent"), `device_detail.html` uses a `.callout` class that has no CSS rule, and `kpi-ok` is applied to the Terrain "Blocked" card with reversed semantics (more blocked = green border, with no explanation).
- **The single biggest recommendation:** reshape the nav around three top-level concepts — **Fleet** (managed hosts, with `Pending` as a sub-tab and `Containers`/`Alerts` as cross-host secondary views under it), **Network** (discovered devices + DNS/DHCP terrain merged), and **System** (events, server health, backups, settings). Today's flat 8-link bar is what you get when nothing is grouped.

## 2. Page Inventory

Navbar (defined in [partials/layout.html](../../internal/server/http/templates/partials/layout.html#L13-L28)): Dashboard · Clients · Devices · Containers · Alerts · Pending · Events · Terrain. No grouping, no settings/admin menu, no theme toggle, no user menu (username + logout button rendered inline).

| Route | Template | Purpose | Primary data | Key actions | Operator task |
|---|---|---|---|---|---|
| `/` | dashboard.html | Triage overview | 6 KPI cards, "Needs attention" tiles, approved clients table, top event sources, recent devices, server self-health | Click KPI → page | "What needs my attention right now?" |
| `/login` | login.html | Auth | Username + password | Sign in | Get in |
| `/setup` | setup.html | First-boot admin bootstrap | Username + password + confirm | Create account | Once-only |
| `/clients` | clients.html | List of approved agents | hostname, IP, OS, version, description, tags, last heartbeat | Deregister (per row) | "Are all my managed hosts up?" |
| `/clients/{id}` | client_detail.html | Per-agent everything | metrics summary, CPU chart, device inventory across 7 conditional tabs, edit description/tags, deregister | Edit notes/tags, deregister | "Drill into one host" |
| `/pending` | pending.html | Agents awaiting approval | hostname, OS, version, registered time | Approve | "Let new hosts in" |
| `/devices` | devices.html | Discovered network devices (grouped by NIC family) | hostname/IP/MAC/vendor/kind/desc/tags/first-seen/last-seen/via-agent | (none — view only) | "What's on my network?" |
| `/devices/{id}` | device_detail.html | Per-discovered-device deep view | identity, multi-NIC roll-up, DNS activity, mDNS service breakdown (Eureka/IPP/Roku/AirPlay/LDAP/SSHFP/DHCP/probes), aliases, sightings | Edit notes/tags | "Identify this device" |
| `/containers` | containers.html | Cross-host Docker container list | name, state, health, image, project/service, host, started | Filter by search/state/project | "Which containers are running where" |
| `/containers/{agentID}/{containerID}` | container_detail.html | One container's static config | identity, lifecycle, composition, config, limits, ports, networks, mounts, labels, engine | (none) | "Inspect this container" |
| `/alerts` | alerts.html | Firings + rules + channels | active firings table, rules table + new-rule form, channels table + new-channel form | Create/delete rules, create/delete channels | "Set up monitoring" + "What just fired?" |
| `/events` | events.html | Append-only event log | time / type / summary, no severity icon | (none — no filter, no pagination, no export) | "What happened recently?" |
| `/terrain` | terrain.html | Upstream DNS/DHCP server stats | server kind/URL/reachability, DNS top queried/blocked/clients, DHCP scope + leases | (none — config lives in `server.toml`) | "Is my home DNS box healthy?" |
| `/api/v1/ui/...` | n/a | JSON for charts and live terrain status | — | — | — |

**Deep-only reachable pages (no nav entry, no in-page link from list):** none — every page is in the navbar. **Missing pages that REQs imply but the UI doesn't expose:** TLS posture / cert management UI (REQ-052), backup download / restore wizard (REQ-046, REQ-049), agent auto-update status (REQ-045), event export (REQ-051), retention controls (REQ-022), quiet hours / dedup config (REQ-025, REQ-026), notifications-on-new-registration (REQ-010 — surfaced via channels but not configurable separately).

## 3. Information Architecture Assessment

### 3.1 Top-level navigation is flat and miscategorized

Eight peer links in [partials/layout.html lines 15-23](../../internal/server/http/templates/partials/layout.html#L15-L23). Problems:

- **`Pending` is a substate, not a peer.** A pending agent IS a client; it just hasn't been approved. Today's user has to look in two places ("is this new host here yet? — no, not on /clients … oh, it's on /pending"). Pending belongs as a tab or filter under `/clients`, with a numeric badge on the parent label when count > 0.
- **`Containers` is a property of a host, not a peer concept.** The cross-host view is useful, but it duplicates the Docker tab on client detail. Treating it as a top-level navigation peer of `Clients`/`Devices` suggests it's its own domain.
- **`Terrain` is upstream infrastructure, not part of the fleet.** It's a passive read of an external DNS/DHCP box. Burying it inside a "Network" group with `/devices` would reduce nav weight and match the mental model ("things about the network").
- **`Events` is product telemetry, not an operational target.** It belongs with server health / system, not next to the things being monitored.
- **`Alerts` is a configuration page that also shows runtime firings.** The firings half belongs near the dashboard; the rules/channels halves belong under settings.

Suggested grouping (concrete proposal in §6):

```
Dashboard
Fleet ▾   Hosts | Pending | Containers | Alerts firings
Network ▾ Devices | DNS / DHCP (formerly "Terrain")
System ▾  Events | Alert rules | Channels | Backups | TLS | Server health
```

### 3.2 Terminology is inconsistent

- "Agent" vs "client" vs "host": dashboard KPI says **"Agents online"** ([dashboard.html L13](../../internal/server/http/templates/dashboard.html#L13)), nav says **"Clients"** ([layout.html L16](../../internal/server/http/templates/partials/layout.html#L16)), pending copy says **"No agents are awaiting approval."** ([pending.html L26](../../internal/server/http/templates/pending.html#L26)), containers page has a **Host** column ([containers.html L36](../../internal/server/http/templates/containers.html#L36)) which links to `/clients/{id}`. Pick one ("agent" matches the product name and the data model; "client" is ambiguous because it also means "DNS client" on Terrain).
- "Device" overloaded: the inventory tab on a client detail is titled **"Device Inventory"** ([client_detail.html L51](../../internal/server/http/templates/client_detail.html#L51)) — but `/devices` lists discovered NICs on the network. Same word, two different referents.
- "Key Cyber Terrain" is jargon that won't parse for the stated single-operator audience. Rename.

### 3.3 Detail-page organization is inconsistent

- `client_detail.html` uses **tabs** inside a single `inventory` section, but the tab set is **dynamically constructed** ([L53-59](../../internal/server/http/templates/client_detail.html#L53-L59)) — `Docker`, `Activity`, `System` etc. appear only when data exists. The tab list will not look the same on two different hosts; users can't form a stable mental map.
- `device_detail.html` uses **stacked sections** (Identity, Description, Interfaces, DNS, mDNS, plus 8+ conditional protocol sub-sections, then Aliases, Sightings). No tabs, no anchor nav. Two detail pages doing fundamentally similar things (one entity, many facets) chose two different patterns.
- `container_detail.html` uses **`.detail-grid` cards** (auto-fit minmax 360px) — a third pattern.

Three peer "show me everything about one thing" pages, three different layout systems. Pick one.

### 3.4 URL structure roughly matches the model

`/clients/{id}`, `/devices/{id}`, `/containers/{agentID}/{containerID}` — the container URL is the only odd one (compound key), and it leaks the storage shape. A stable per-container ID would be cleaner; if there isn't one, encode it as `/containers/{combined}` and resolve server-side.

## 4. Page-by-Page UX Findings

Severity: **S1** = blocks core task / broken / accessibility violation; **S2** = significant friction or inconsistency; **S3** = polish.

### Dashboard ([dashboard.html](../../internal/server/http/templates/dashboard.html))

- **S2** [L9-L40] Six KPI cards is on the edge. Only Agents/Stale/Pending/Alerts have severity logic; Devices and Containers never light up (no warn/danger state). The result reads as "4 active sensors + 2 always-cold cards," which makes the colored ones less salient. Either give every KPI a meaningful severity rule (e.g., `kpi-warn` when device dedup queue is long) or drop the inert ones from the strip.
- **S2** [L24-L28] `Pending` card uses `kpi-attn` (the accent blue). That's the same blue as the active nav border ([app.css L46](../../internal/server/http/static/app.css#L46)). Visual collision — the eye is drawn to whatever is blue, including the current nav item.
- **S2** [L86-L104] "Approved clients" duplicates `/clients`. On a dashboard you want triage, not the full list. Either cap to top-5 most-recently-heartbeat, top-5-stalest, or remove and link.
- **S2** "Needs attention" tiles cover stale, pending, unhealthy containers, ungrouped devices — but **not** open alert firings. The KPI card for open alerts goes red, but the alerts themselves aren't surfaced in the attention feed. The page promises a triage view and then routes the most important triage signal off to a separate page.
- **S3** [L160-L167] "Server" section uses a `<dl>` with `kv` styling — fine — but it's the only place the operator can see DB size and uptime. Belongs in a System / Health page, not glued to the bottom of the daily-driver dashboard.

### Clients ([clients.html](../../internal/server/http/templates/clients.html))

- **S2** [L24-L29] Per-row `Deregister` button using only `confirm('Deregister X?')`. Destructive, irreversible, one mis-click away. Move to the detail page only (it's already there at [client_detail.html L416](../../internal/server/http/templates/client_detail.html#L416)). For bulk operations, use a checkbox + bulk action footer.
- **S2** [L7] No status column. The table shows `Last heartbeat` as a relative string ("3m") and expects the user to mentally classify it. Add an explicit status badge (online / stale / offline) using the same thresholds the dashboard uses.
- **S2** No filter / no search. The page will get unusable past ~30 hosts.
- **S3** Headers sort on click but the table doesn't pre-sort by anything obvious; "Last heartbeat" descending would be a reasonable default.

### Client detail ([client_detail.html](../../internal/server/http/templates/client_detail.html))

- **S1** [L53-L59] Tab list is dynamic — the user cannot rely on the same tabs being present across hosts. At minimum, render all tabs always and show "—" / "Not collected" inside; or surface a single "Inventory completeness" indicator instead of letting tabs vanish. Today's behavior also means the tab `#hash` URL is unstable.
- **S2** [L4-L5] Page header is a single `<h1>` plus a muted `<p>` with bullet-separated metadata. No status badge (online / stale). Status of THIS host is the single most important piece of information; it should be a visual element next to the name.
- **S2** [L8-L22] "Description & tags" comes before "Latest metrics." Time-sensitive data should lead.
- **S2** [L49] CPU canvas chart has no x-axis time labels, no tooltip, no legend in light theme (`#30363d` grid color is hard-coded — [app.js L78](../../internal/server/http/static/app.js#L78)). REQ-021 promised sparklines for "key metrics" — only CPU is charted.
- **S2** [L13-L21] Edit form uses bare `<details><summary>Edit</summary>...` — `/alerts` uses the styled `.form-details` collapsible pattern. Two patterns for the same affordance.
- **S3** [L414-L420] Same deregister action repeated; consolidate.

### Devices ([devices.html](../../internal/server/http/templates/devices.html))

- **S2** [L7] 10-column table. On a 768px tablet this requires horizontal scroll, and there is no overflow wrapper — page will overflow viewport.
- **S2** [L11-L15] Hostname cell falls back to IP / MAC / "unknown" with a `(no hostname)` muted note. The link text varies by row, which weakens the scannability of the first column.
- **S2** [L40-L46] "Via" column links to the discovering agent, but `AgentID` falls back to `SeenByAgent` — the semantics of these two fields are not visible to the user. Is this device managed (it's one of my agents) or merely discovered? Today only the device-detail page makes that clear via the `.callout`.
- **S3** No filter for kind / vendor / VLAN / "ungrouped only" — the dashboard surfaces an "ungrouped" attention tile that links here but there's no way to filter to just those.

### Device detail ([device_detail.html](../../internal/server/http/templates/device_detail.html))

- **S1** [L8] `<div class="callout">` — `.callout` has **no CSS rule** in `app.css`. The "Managed client" announcement renders as plain text. Either add the styles or drop the class.
- **S2** [L131-L195] Each mDNS sub-protocol (Eureka, IPP, Roku, AirPlay, LDAP, SSHFP, DHCP, plus a `.Probes` map) gets its own `<section>` with its own `<h2>`. For an mDNS-rich device you can get 8+ identical-looking K/V sections stacked vertically. Consolidate into one "Service profiles" section with a single header and per-protocol sub-headings (or tabs).
- **S2** [L239-L259] "Sightings" is at the bottom — for a discovery view, recent sightings are arguably what the operator came to see. Promote.
- **S3** [L72-L88] When `Members > 1`, the table re-lists every NIC in the group; each row links back to `/devices/{id}` for the same group. Risk of accidental navigation loops; mark the current row as non-link.

### Containers ([containers.html](../../internal/server/http/templates/containers.html))

- **S2** [L7-L13] Stats cards use `.card-num` (28px) but dashboard cards use `.card-value` (28px). Two CSS classes for the same visual; pick one.
- **S3** [L15-L29] Search is plain `q=` matching name and image. Workable; consider adding "host" and "label key=value" filters since labels are collected on detail.

### Container detail ([container_detail.html](../../internal/server/http/templates/container_detail.html))

- **S3** This page is consistent with itself and uses the `.detail-grid` pattern cleanly. Worth keeping as a model for the other detail pages.
- **S3** No "View on host" cross-link beyond the host link in Identity — would be useful to surface "siblings on same compose project" inline.

### Alerts ([alerts.html](../../internal/server/http/templates/alerts.html))

- **S1** [L1-L160] Three concerns on one page: firings (operational), rules (config), channels (config). Split into tabs (`Firings | Rules | Channels`) under an `Alerts` parent, or — preferably — move firings to the dashboard / a top-level Alerts page and put rules + channels under `Settings`.
- **S2** [L58-L62] Metric `<select>` is hard-coded to `cpu_pct | mem_pct | offline_minutes`. REQs promise more (bandwidth REQ-028, disk I/O REQ-029, SMART REQ-030). Either gate this UI behind the implemented set with a "more metrics coming as agents report them" hint, or load the metric list from the server.
- **S2** [L114-L118] Channel kind is `webhook | email`. REQ-024 names Discord, Pushover, Gotify. Same gap.
- **S2** No UI for dedup window (REQ-025) or quiet hours (REQ-026), despite those being explicit "should-have" requirements.
- **S2** [L33-L42] Firings table has no filter (by rule, by agent, by open vs resolved), no pagination. Will get unusable as soon as a noisy rule fires for a week.
- **S3** [L154-L160] Inline `<script>` for kind-toggle. Move to `app.js` (becomes reusable, and your CSP can drop `'unsafe-inline'` for scripts in future).

### Pending ([pending.html](../../internal/server/http/templates/pending.html))

- **S2** [L18-L20] Approve only. No explicit reject — operator must wait for expiration (REQ-009) or rely on it. Add a "Reject" action.
- **S3** No bulk approve, no detail link for a pending agent (you can't see what an agent claims it is before approving — only hostname/OS/version are shown).

### Events ([events.html](../../internal/server/http/templates/events.html))

- **S2** No filter (by severity, by type, by source), no pagination, no export. REQ-051 promises JSON/CSV export — no button.
- **S2** Severity is encoded as a color on `.evt-summary` text only ([app.css L82-L83](../../internal/server/http/static/app.css#L82-L83)). No icon, no badge. Color-blind users get nothing. Add a severity badge in its own column.
- **S3** 3-column grid (`160px 200px 1fr`) without a real header row — the time column width is fixed in CSS without respecting viewport scaling.

### Terrain ([terrain.html](../../internal/server/http/templates/terrain.html))

- **S2** Page title **"Key Cyber Terrain"** is wrong-register. Rename to "Network status" or "DNS / DHCP."
- **S2** [L12, L25, L33, L110, L114, L118, L122] Repeated inline `style="font-size:..."` overrides on KPI cards. Bypasses the design system; introduce a `.kpi-compact` (or whatever) modifier and use it.
- **S2** [L20] `kpi-ok` class fires when `BlockedQueries > 0`. Semantics: more blocks = good (ad blocker working). This is defensible but the badge color is the same green as "Agents online" — without context, a user could read "lots of blocks" as "ok = nothing to worry about" or as "warning = lots happening." Add an inline explanation ("blocking is healthy") or use a neutral accent.
- **S3** Page is read-only with no link to where the config lives ("Set `[terrain].url` in `server.toml`" appears once in muted copy on the unreachable state but is otherwise hidden).

### Login / Setup

- **S1** [partials/layout.html L53](../../internal/server/http/templates/partials/layout.html#L53) `<title>{{.Title}}JMW Agent</title>` — missing the `· ` separator the authenticated `head` block uses. Renders "Sign inJMW Agent" / "Welcome to JMW AgentJMW Agent". Quick fix.
- **S3** Setup page has no password strength meter and `minlength="6"` (login.html L9 / setup.html L9) — six characters is below most modern guidance.

## 5. Cross-Cutting Patterns

### 5.1 Design system

- **Tokens exist and are reasonable.** [app.css L1-L29](../../internal/server/http/static/app.css#L1-L29) defines a sensible dark + light palette via CSS custom properties.
- **But hard-coded hex colors leak through.** Badge states use `#2dd4a4`, `#94a3b8`, `#fbbf24`, `#f87171` ([app.css L143-L153](../../internal/server/http/static/app.css#L143-L153)). The grid color in `app.js` chart is hard-coded `#30363d` ([app.js L78](../../internal/server/http/static/app.js#L78)). These do not adapt to light theme. Move all of them to tokens (`--state-ok`, `--state-warn`, `--state-bad`, `--chart-grid`) with `[data-theme="light"]` overrides.
- **Two card systems coexist** (`.card-value` vs `.card-num`, sized identically) — pick one.
- **Two collapsible-form patterns coexist** (`<details>` plain vs `.form-details`).

### 5.2 Theme & responsive

- **Theme toggle is dead code.** `app.js initTheme()` reads `localStorage` and applies a theme, but `<html data-theme="dark">` is hard-coded in `layout.html` head ([L2](../../internal/server/http/templates/partials/layout.html#L2)) and there's no UI control to set the preference. Light-theme CSS exists but is unreachable from the product. Either ship a toggle (a navbar icon-button is enough) or delete the light theme until it's wired up.
- **Mobile/tablet behavior is not designed.** The navbar at 8 links wraps; tables of 8-10 columns overflow. The only media query in the entire stylesheet is `@media (max-width: 900px)` on `.two-col` ([app.css L177](../../internal/server/http/static/app.css#L177)). REQ-019 explicitly calls out "responsive UI" — this is the largest single shortfall against requirements.
- **Tables need horizontal-scroll wrappers.** A `<div class="table-scroll">` wrapper with `overflow-x: auto` on the data tables would prevent layout breakage with minimal effort.

### 5.3 Empty / loading / error states

- **Empty states** are consistent (a single `<p class="muted">No X.</p>`) and have decent copy in places (pending page especially). Few of them give the user a next action ("install an agent → docs" would help on `/clients`).
- **Loading states** don't really exist — server-rendered pages don't need one, but `renderCpuChart()` ([app.js L29-L37](../../internal/server/http/static/app.js#L29-L37)) silently returns on `!res.ok`. A user with a stalled fetch sees a blank canvas with no explanation.
- **Error states** are minimal: login's "Login failed. Please try again." is the only user-facing one. Setup, edit forms, alert creation — none show server-side validation errors; on failure the user just sees the form re-render (or a 4xx page from chi).

### 5.4 Accessibility

- **Title bug** (S1, noted above) breaks `<title>` on auth pages — minor but symptomatic of low-test rigor on HTML.
- **Tabs**: `role="tab"`, `aria-selected`, `data-tabs`, `hidden` panels — mostly correct. Missing `aria-controls` linking each tab to its panel, and missing `role="tablist"` (the class `.tablist` is present but `role` isn't). Keyboard arrow-key navigation between tabs isn't implemented (`app.js initTabs` only listens for click).
- **Forms** mix two label patterns: wrapping (`<label>X<input/></label>`) and `for=` ID linking (the edit forms). Either works; pick one.
- **Destructive actions** rely on `onsubmit="return confirm('Deregister X?')"` — accessible but the message is terse and gives no information about what data is preserved/lost.
- **Color contrast**:
  - Dark theme `--fg-muted #8b949e` on `--bg-elev #161b22`: ~5.0:1 — passes AA for body text, borderline for small.
  - Light theme: same muted on `--bg-elev #f6f8fa`: ~5.1:1 — okay.
  - Badge text in hard-coded `#2dd4a4` on light `--bg-elev #f6f8fa`: ~2.5:1 — **fails AA**. Confirms the badge-token gap above.
- **Focus styles**: inputs get an accent outline; links rely on hover underline alone — no `:focus-visible` rule. Keyboard-only users can't see where focus is on inline links.
- **No skip-to-content link.** Each page begins with the same navbar; screen-reader / keyboard users have to tab past 8 nav links every navigation.

### 5.5 Density

- Mostly right. Tables are tight but readable.
- Client detail page is dense **only when the host returned a lot of inventory** — the tab system mitigates this, but the conditional tabs (§4) hurt learnability.
- Dashboard is borderline busy: KPI strip + needs-attention grid + approved-clients table + two-col panels + server health — five vertical sections. A two-pane "today / fleet" layout would feel less like a log.

## 6. Prioritized Recommendations

Top-to-bottom by impact. If you stop after #5 you'll have addressed the biggest user-facing problems.

1. **Restructure top-level navigation into three groups: Fleet / Network / System.** *(Why: §3.1, §3.2 — current 8-link flat bar conflates substates, properties, and upstream infra. Effort: M. Approach: introduce nested nav (visible parents with dropdowns on hover/click, or just bolder section labels in the sidebar if you go sidebar). Move `Pending` to a tab under Fleet > Hosts. Move `Containers` and live alert `Firings` under Fleet. Move `Devices` and rename `Terrain` → "DNS / DHCP" under Network. Move `Events`, alert `Rules`, `Channels`, future `Backups`/`TLS`/`Settings` under System.)*

2. **Fix the dead-code theme toggle and the light-theme contrast bugs.** *(Why: §5.1, §5.2 — REQ-019 promises dark mode default + responsive; ship a toggle (icon-button in navbar right) and move hard-coded badge hex colors into `--state-*` tokens with light overrides. Also fix the auth `<title>` typo and the `.callout` missing CSS while you're in there. Effort: S.)*

3. **Add mobile/tablet responsiveness — at minimum a collapsing navbar and table scroll wrappers.** *(Why: §5.2 — REQ-019. Effort: M. Approach: hamburger pattern at `<800px`, wrap every `.data` in `.table-scroll { overflow-x: auto; }`, drop or stack low-priority columns at narrow widths.)*

4. **Split `/alerts` into three concerns and load metric/channel options dynamically.** *(Why: §4 Alerts findings — single page mixes runtime + config; hard-coded options under-promise vs REQs. Effort: M. Approach: route `/alerts` to firings only with filters; move rules + channels to `/settings/alerts` with tabs; ask the server for available metrics and channel kinds rather than hard-coding in the template.)*

5. **Pick one term per concept and apply it everywhere.** *(Why: §3.2. Effort: S textual + S template renames. Recommendation: `Agent` for managed-host-with-our-software; `Device` for discovered-network-thing; `Host` only as a column label inside Container views; retire "client". Rename "Key Cyber Terrain" → "DNS / DHCP".)*

6. **Add an explicit Status column / badge to `/clients` and to the client-detail header.** *(Why: §4 Clients, Client detail. Effort: S. Approach: badge online/stale/offline using the same thresholds as the dashboard KPIs.)*

7. **Stabilize the client-detail tab set.** *(Why: §4 Client detail — dynamic tabs are unlearnable. Effort: S. Approach: always render the full tab set; show "Not collected" inside missing-data tabs; add a single "Inventory completeness" badge near the header for the missing-data signal.)*

8. **Move destructive actions off the list view.** *(Why: §4 Clients S2 — per-row Deregister with only `confirm()`. Effort: S. Approach: remove the inline button on `/clients`, keep the one on `/clients/{id}`. If you want bulk operations, add a checkbox column + footer action bar.)*

9. **Make the `/events` page operable: severity badge column, severity filter, source filter, pagination, JSON/CSV export button.** *(Why: §4 Events; REQ-051. Effort: M.)*

10. **Add backups / TLS / retention surfaces under a new `/settings` area.** *(Why: §2 missing pages, REQ-022/046/049/052. Effort: L overall but small per surface. Start with a Backups page that exposes the manual-download button — REQ-046 is should-have and trivially user-visible.)*

11. **Consolidate the `device_detail.html` mDNS sub-protocol sections.** *(Why: §4 Device detail S2. Effort: S. Approach: one "Service profiles" section with sub-headings or a tab strip; the existing data unchanged.)*

12. **Move `Sightings` and `DNS activity` higher on device detail.** *(Why: §4 Device detail S2. Effort: trivial.)*

13. **Add a real chart legend, axis labels, and tooltips to the CPU chart; chart memory and load too.** *(Why: §4 Client detail S2; REQ-021. Effort: M. Approach: keep the canvas-based renderer or drop in a small chart helper; either way add x-axis time ticks and a hover tooltip layer.)*

14. **Consolidate the design system: one card class, one collapsible-form class, all colors via tokens.** *(Why: §5.1. Effort: S–M depending on how aggressive the dedup.)*

15. **Surface open alert firings in the dashboard "Needs attention" feed.** *(Why: §4 Dashboard — the most actionable signal is currently absent from the triage view. Effort: S.)*

16. **Add filters to `/clients`, `/devices`, and the alerts firings table.** *(Why: §4 each. Effort: S each; common pattern.)*

17. **Inline `<script>` in `alerts.html` → move to `app.js`; remove the inline `style="..."` on Terrain KPI cards.** *(Why: §5.1; future CSP hardening. Effort: S.)*

18. **Add `:focus-visible` outlines on links + a skip-to-content link.** *(Why: §5.4. Effort: trivial; gives keyboard users a fighting chance.)*

19. **Add `aria-controls` on tabs and arrow-key navigation in `app.js initTabs`.** *(Why: §5.4. Effort: S.)*

20. **Add explicit "Reject" action on `/pending`.** *(Why: §4 Pending S2. Effort: S.)*

## 7. Open Questions

These would change recommendation priorities; I couldn't determine them from code:

- **What does the operator actually do in this UI day-to-day?** If the answer is "glance at the dashboard once a week and get notified by Discord otherwise," the responsive / mobile work is critical and the alerts UI matters less. If the answer is "tune alert rules constantly," the opposite.
- **Are there any other users?** Requirements say single-operator, but the auth model and the framing of `/settings` would change if multi-user is ever in scope.
- **Will operators view this on a tablet in a homelab rack / NOC, or only on a desktop?** Determines how aggressive the responsive redesign should be.
- **Is the absence of metric variety in `/alerts` a UI gap or a backend gap?** If the metrics are already collected (REQs 021/028/029/030 are implemented) and only the rule form is gated, that's a 1-line template change; if the backend doesn't evaluate them yet, the UI is honestly representing the gap.
- **What's the planned home for backups / TLS / retention UI?** If there is no plan to expose any of REQ-022/046/049/052 in the UI (and they remain CLI/config-only), then the "System / Settings" group in the nav restructure is mostly empty and the nav grouping argument weakens.
- **Is "Key Cyber Terrain" a deliberate brand stake or a working title?** If deliberate, ignore §3.2's rename suggestion; if working, please change it.
- **Should `/containers` continue to exist as a top-level cross-host view, or is the per-host Docker tab sufficient?** Determines whether item #1's nav restructure keeps it.
