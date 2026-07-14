---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 9
status: draft
---

# Screen Specifications — JMW Agent Facts Server

Each screen implements the component and pattern decisions in `usability-design.md`. Terminology follows `glossary.md`. Screen IDs match the inventory in `information-arch.md`.

Shared chrome (all authenticated screens, SCR-003 onward): role-aware left sidebar (per `information-arch.md`), a page header with title + last-updated timestamp (where applicable) + page-level actions, and a session footer. Reporting tables share the Family A pattern: toolbar filters + free-text search, sortable column headers, keyset pagination (100/page), status cells with icon+label+color.

---

## Family D: Authentication

### SCR-001: Login

**Purpose:** Authenticate an operator or viewer into a server-side session.
**Primary user goal:** Sign in.
**Entry points:** Unauthenticated access to any URL redirects here.

**Content areas:**
- Centered login card — username + password fields, Sign In button. (Single centered form; the one screen where centered is correct — no fleet data to be dense about.)
- Product wordmark above the card.
- Inline error region below the form for failed auth.

**Key interactions:**
- Submit valid credentials → server-side session established (httpOnly cookie, REQ-001) → redirect to Fleet Dashboard.
- Submit invalid credentials → inline error ("Invalid username or password"), fields preserved (password cleared), no detail that reveals which field was wrong.

**Data shown:** none (pre-auth).

**States:**
| State | Description |
|-------|-------------|
| Populated | Empty form ready for input. |
| Loading | Sign In button shows a spinner/disabled state during auth round-trip. |
| Error | Inline "Invalid username or password"; generic, no account enumeration. |
| Rate-limited | If repeated failures, a "too many attempts, try again shortly" message. |

**Navigation context:** Root unauthenticated screen; no sidebar.
**Related flows:** precedes all flows.

### SCR-002: First-Run Bootstrap

**Purpose:** On first deployment, set the admin password using the one-time console token (DEC-003).
**Primary user goal:** Establish the admin account.
**Entry points:** First run only; the server prints a one-time token to the console; visiting the app pre-account routes here.

**Content areas:**
- Centered card — one-time token field, new password + confirm-password fields, Create Admin Account button.
- Explanatory text: where to find the token (server console/log).

**Key interactions:**
- Valid token + matching passwords → admin account created → redirect to Login (SCR-001).
- Invalid/expired token → inline error.

**Data shown:** none.

**States:**
| State | Description |
|-------|-------------|
| Populated | Bootstrap form. |
| Error | "Invalid or expired token" / "Passwords do not match." |
| Already bootstrapped | If an admin already exists, this route redirects to Login (the one-time path is consumed). |

**Navigation context:** Unauthenticated, one-time.
**Related flows:** precedes Login.

---

## Fleet Dashboard

### SCR-003: Fleet Dashboard

> **Redesigned (iteration 2, dashboard-redesign pass).** The original flat 9-tile grid
> (label + big number, three concerns mixed, two tiles hardcoded to 0) is replaced by a
> grouped, hierarchical, intent-organized layout. Rationale and panel-by-panel detail below.
> The IA-level panel map and the new backend aggregates this design requires are in
> `information-arch.md` (§ Fleet Dashboard panels and § New backend aggregates).

**Purpose:** At-a-glance fleet health and situational awareness; the landing page after login (REQ-010).
**Primary user goal:** Answer, in order: *Is the dashboard itself trustworthy? What's broken or about to break? Are my agents/collection healthy? What changed recently? What's the shape of my network?* — then jump straight to any problem.
**Entry points:** Post-login redirect; sidebar Dashboard; product wordmark.

**Design principle — reading order is attention-first, not totals-first.** The panels are ordered
by *actionability*, top to bottom, so the operator's eye lands on the scariest, most actionable
content first (F-pattern first fixation) and on reassuring vanity counts last. This inverts the old
grid, which led with "Total Devices" (a number that rarely changes behavior) and buried nothing
actionable because it aggregated nothing. The full justification is in `information-arch.md`.

**Content areas (in reading order):**

- **Page header** — "Fleet Dashboard", last-updated timestamp (`aria-live="polite"`), refresh-interval control (select: 30s / 60s / 5m / off, default 60s).

- **0. Trust banner (conditional, directly under the header).** A single `.banner` that appears only when the dashboard itself cannot be trusted at face value: `.banner.stale` when the newest projection data is older than 2× the collection interval ("Data may be stale — newest fact [ts]"); **`.banner.error` whenever one or more agents are offline** ("1 agent offline · collection may be incomplete") — using `.banner.error` (not `.stale`) so *offline* keeps its critical/red semantics consistently with the agent table below (design-conventions: same word, same color). Rationale: a false "All clear" is worse than a visible warning — the operator must know the feed is degraded *before* trusting the panels below. Absent when everything is nominal. **Backend note:** the existing `_StaleIndicator` partial renders one hardcoded string from a `bool`; it must be extended to accept a message + severity (or a new partial added) to carry the offline count / newest-fact timestamp / `.banner.error` variant this design shows — see `information-arch.md` § New backend aggregates.

- **1. NEEDS ATTENTION (hero, full-width panel).** The actionable posture + data-quality rollup — the single answer to "what's broken or about to break?". Given the most visual weight (top, full width, large severity glyphs). Rows are grouped by concern; each row = severity shape-glyph (`.status.crit`/`.warn`) + count + plain-language label + deep-link to the matching pre-filtered report. Rows with a zero count are hidden (not shown greyed). Concerns:
  - Certificates expiring ≤30 days (`proj_device_certs.not_after`, `proj_service_ca`) → `/certs?status=expiring`.
  - Pending **security** updates (`proj_updates.security`) → `/patches?type=security`; and **reboot required** (`proj_updates.reboot_required`) → `/patches?reboot=required`.
  - Failed hardware (`proj_hardware_inventory.status='failed'`) and bad-SMART disks (`proj_disks.smart_health` not healthy) → `/storage?smart=degraded`.
  - Unhealthy containers (`proj_containers.state`/`health`) → `/containers?state=not-running`; failed/degraded services (`proj_device_services`) → `/services?health=degraded`; filesystems near full (`proj_filesystems.used_pct` ≥ threshold) → `/storage?fs=full`.
  - Security posture gaps (missing firewall/AV/TPM/Secure Boot, `proj_security`) → `/security?compliant=false`.
  - **Fingerprint conflicts** (data quality — device records sharing a fingerprint) → `/admin/conflicts`. Moved here from a standalone vanity tile; it is an actionable "resolve by merge" item, not a network total. **RBAC:** `/admin/conflicts` is Admin-only, so this row renders for admins only and is hidden for the Read-Only Viewer (consistent with the Admin nav group being omitted entirely, not shown-disabled) — the viewer never sees a row that would 403.

- **2. FLEET HEALTH (two columns).**
  - **Agent Health** — carries **all agent counts** (this is the *only* place agent totals appear — they were removed from the Network totals strip to avoid duplication): total / approved / pending agents, plus a heartbeat-derived liveness summary (online / stale / offline — heartbeat age vs 2× expected interval, a value never previously computed). Followed by a compact per-agent list: status pill (online/stale/offline + pending/approved/disabled), hostname (a real link, keyboard-reachable), version, zone, `passive_discovery_mode` (full/degraded shown as a `.status` chip), relative last-heartbeat. Header action → `/admin/agents` (**Admin-only**, hidden for the viewer). Offline/stale agents sort to the top and carry a row rail. **Cap:** large fleets show offline/stale first then a bounded set (~8); the full list is behind the header action.
  - **Collection Health** — fleet rollup of the most recent `agent_cycles` per agent: total facts sent (last cycle), count of agents reporting `error_count > 0`, average cycle duration, and an inline-SVG **error-rate sparkline** over the last N cycles (7-day retention window). Header action → agent detail / cycles. This aggregation exists nowhere today (cycle data is per-agent only).

- **3. RECENT ACTIVITY.** Answers "what changed?" Laid out as two device-liveness lists **side by side** — **Not seen recently** first (it is the more actionable of the pair), then **New devices** — with **Recent changes full-width underneath** them.
  - **Not seen recently** — devices whose newest `last_seen` (`device_fingerprints.last_seen` / `proj_systems.updated_at`) is older than N days. **This replaces the old hardcoded "Devices Stale = 0" tile with real data.** → `/all-hosts?status=stale`. **Cap:** oldest-first ~5.
  - **New devices** — devices first seen in the last few days (`device_fingerprints.first_seen` / `devices.created_at`), count + short list. → `/all-hosts?added=recent`. **Cap:** newest ~5.
  - **Recent changes** (full-width) — last N fact changes from `facts_history` (attribute path, entity, relative `collected_at`), plus a 24h change count. Header action → `/changes` (the page route; the fragment reuses existing `GET /api/v1/report/changes`). **Cap:** newest ~8.

- **4. NETWORK (device totals + composition + trend, lowest weight, bottom).**
  - **Totals strip — four headline tiles** (compact stat row, not oversized hero tiles). Agent totals are NOT here — they live in the Agent Health panel above (no-duplication decision). Each tile uses the same **part-to-whole motif**: a thin segmented bar under the value + a caption naming the parts. **Colour is reinforcement only** (numbers/labels/caption carry the meaning; bars are `aria-hidden`), and follows design-system semantics — *neutral category* splits use the indigo family (`--accent` / `--accent-dim` / faint-grey tail), *health-ratio* splits use the status tokens (`--ok` / `--warn`). No new palette.
    1. **Total devices** = Managed + Discovered (neutral → indigo two-shade). Caption entries "42 managed" / "206 discovered" deep-link per the URL map. The separate Managed/Discovered tiles were removed as redundant now that the split lives in this tile's bar.
    2. **Services** — total logical services (`services` table), broken down **by `services.type`** (neutral → indigo family + faint "other" tail; caption "12 DNS · 9 HTTP · 16 other"). Tile → Services list (SCR-007).
    3. **Reporting (24h)** — devices actively seen in the last ~24h vs gone **quiet**, from `device_fingerprints.last_seen` / `proj_systems.updated_at` (health ratio → `--ok` reporting / `--warn` quiet). This is the headline coverage ratio; the **Recent Activity › Not seen recently** panel is its drill-in list — the overlap is intentional (this tile answers "what fraction is reporting?"; that panel lists the specific quiet hosts, at a longer >7d threshold). "reporting" → `/all-hosts?status=reporting`, "quiet" → `/all-hosts?status=stale`.
    4. **Zones** — distinct network zones the fleet spans, `count(distinct zone)` from `agents` (a cardinality, so no part-to-whole bar). Tile → `/admin/agents`. **Alternative/richer source (implementer's choice):** derive segments from the network terrain instead — e.g. `proj_dhcp_scopes` / `proj_device_routes` subnets — if a truer "network segments" count is wanted.
  - **Composition** — network breakdowns as labeled horizontal `.bar` rows: devices by **vendor**, by **os_family**, and by **kind** (top-N + "other"). Group-bys over `proj_devices` / `proj_systems`. Each row is a **link** to All Hosts filtered by that dimension. **Follow-on note:** the vendor (OUI) filter exists on All Hosts (SCR-004) today, but `os` and `kind` filter dimensions must be *added* to the SCR-004 toolbar for those two breakdowns to deep-link into a filtered view — flagged as a follow-on requirement in `information-arch.md`.
  - **Change trend** — inline-SVG sparkline of fact-changes-over-time (recent days) for light situational context. No heavy charting library — inline SVG only.

**Key interactions:**
- Click any Needs-Attention row, device-totals tile, composition bar row, agent row, or activity item → navigate to the matching reporting page, pre-filtered (URL convention in `information-arch.md`). Agent rows and the agent-panel header action are Admin-only targets — hidden for the Read-Only Viewer.
- Each panel refreshes **independently** via its own htmx fragment (`hx-get="/dashboard?fragment=<panel>"`, `hx-trigger="every <interval>s"`, `hx-swap="outerHTML"`) so a slow or failing panel never blocks the others, and volatile panels can poll faster than slow-moving ones. Cadence per panel: Needs Attention 60s, Fleet Health 60s, Recent Activity 30s, Network 5m.
- Change refresh interval → all panel triggers update in place without resetting scroll or stealing focus.

**Data shown:** posture rollup counts (certs, updates, hardware/SMART, containers/services/filesystems, security gaps, fingerprint conflicts); agent health (heartbeat-derived online/offline, status, version, zone, passive mode); collection rollup (facts_sent, agents-with-errors, avg duration, error-rate series); recent changes; new devices; not-seen devices; device/agent totals; composition group-bys; change-trend series; last-updated timestamp; staleness flags.

**States (per panel — each fragment renders its own):**
| State | Description |
|-------|-------------|
| Populated | Grouped panels; Needs Attention shows only non-zero concerns; Fleet Health lists agents with offline/stale first. |
| Empty (whole page) | Fresh install, no agents yet: Needs Attention shows an `.empty` "All clear — no agents reporting yet" and a prominent "Register your first agent" CTA → Admin › Agents (admin) or an informational note (viewer). Totals show 0. |
| Empty (Needs Attention, healthy fleet) | `.empty` "All clear — no attention items" (positive confirmation, not a blank panel). |
| Empty (a list panel) | New devices / Not seen / Recent changes each show a one-line `.empty` ("No new devices in the last 7 days"). |
| Loading | Each panel shows `.skeleton` placeholders on first load (independent per fragment). |
| Error (per-panel) | A panel whose fragment query fails renders the shared `_SectionError` partial **within that panel only** — a `.empty` block reading "Failed to load this section: [message]"; sibling panels still render and refresh (REQ-028). |
| Stale | Trust banner (area 0) uses the shared `_StaleIndicator` partial — a `.banner.stale[role=status]` "Data may be stale…" above all panels (REQ-028); panels still render their last values, flagged. |

**Navigation context:** Top of sidebar; root of the scan loop.
**Related flows:** FLOW-001 (origin — the morning triage / daily fleet scan).

---

## Family A: Cross-fleet reporting tables

All Family A screens share: toolbar filters + search, sortable headers, keyset pagination (100/page), status cells (icon+label+color), per-page last-updated timestamp, auto-refresh consistent with the dashboard cycle. Empty/loading/error/stale states follow the same template (stated once here, referenced per screen).

**Shared states (Family A):**
| State | Description |
|-------|-------------|
| Populated | Dense table of rows matching current filters; sort/pagination active. |
| Empty | Meaningful empty state: "No [items] match the current filters" + a clear-filters link; or, if truly no data exists, an explanatory note. |
| Single item | Renders normally as a one-row table; no special layout. |
| Loading | Table-body skeleton rows; filter bar interactive. |
| Error | Inline error banner above the table describing what failed + retry; filter bar still usable (REQ-028). |
| Overflow (1,000+) | Keyset pagination, 100/page; Next/Previous controls; sort+filter served by indexes (no offset scans). |
| Stale | "Data may be stale" banner (REQ-028). |

### SCR-005: Device List — ⚠️ SUPERSEDED

**Superseded by SCR-004 (All Hosts).** This screen was dropped as redundant: it rendered the same canonical-device population (managed + discovered) as All Hosts, with no differentiator built (the intended health-indicator lens per REQ-011 was never implemented). All Hosts now serves as the single device inventory drill-down root under Inventory. REQ-011 is marked superseded (see `requirements/REQ-011-device-list.md`); its device-health-list intent, if revived, should extend SCR-004 rather than reintroduce a second list. The original spec is retained below for history.

**Purpose:** Browse all devices (managed + discovered) as the inventory drill-down root (REQ-011).
**Primary user goal:** Find a device and open its detail.
**Entry points:** ~~Inventory › Devices; dashboard "offline devices" card~~ — folded into SCR-004 All Hosts.

**Content areas:**
- Toolbar: filter by management status (managed/discovered, REQ-030), zone, online/offline; free-text search (hostname/IP).
- Table columns: Hostname, IP, Management Status (badge: solid=managed, outline=discovered), Zone, OS (blank for discovered), Online/Offline, Last Seen, Collecting Agent (blank for discovered).

**Key interactions:**
- Click row → Device Detail (SCR-006).
- Filter/sort → updates URL; state restored on reload.

**Data shown:** device identity, management status, zone, OS, liveness, last-seen, agent.
**States:** Family A shared states.
**Navigation context:** ~~Inventory group~~ — superseded.
**Related flows:** FLOW-002.

### SCR-004: All Hosts ⟶ novel screen

**Purpose:** The unified managed + discovered host inventory with provenance/confidence cues and inline promotion (REQ-037).
**Primary user goal:** See every host, judge how well each discovered host is characterized, promote worthwhile ones.
**Entry points:** Inventory › All Hosts; dashboard managed/discovered count cards (`?status=managed` / `?status=discovered`).

**Content areas:**
- Toolbar: filter by management status, discovery source (ARP/mDNS/LLDP/NBNS/DHCP), OUI vendor prefix; free-text search (hostname/IP). Sort by hostname, IP, last seen, status, vendor (REQ-037).
- Table columns: Hostname(s), IP Address(es), MAC, OUI Vendor, Management Status (badge: solid=managed, outline/muted=discovered — shape carries meaning, not color alone), Discovery Sources (compact tag list — tag count is the at-a-glance confidence cue), Last Seen (most recent signal from any source; muted/warning if "possibly offline" per REQ-038), Agent Name + OS (managed only; "—" for discovered).
- Inline action column: Promote button on discovered rows only, Admin role only (hidden for viewer and for managed rows).

**Key interactions:**
- Click row → Device Detail (SCR-006).
- Click Promote (discovered row, admin) → Promotion modal (SCR-022).
- Filter by discovery source / vendor / status → URL-encoded.

**Data shown:** REQ-037 columns; discovery source set; last-seen recency; OUI vendor; for managed: agent + OS.

**States:**
| State | Description |
|-------|-------------|
| Populated | Mixed table: solid-badge managed rows (OS/agent populated) interleaved with outline-badge discovered rows (OS/agent "—", 1–N source tags). Realistic scale ~250 rows across pages. |
| Empty | "No hosts known yet" — discovery sources have not yet reported anything; explanatory note. |
| Single item | One row (likely the server's own host on first run). |
| Loading | Skeleton rows; toolbar interactive. |
| Error | Inline error + retry; toolbar usable (REQ-028). |
| Overflow (1,000+) | Keyset pagination 100/page; loads ≤2s for 1,000 devices (REQ-037). |
| Possibly-offline rows | Last-Seen cell shown muted/warning when no source within 2× the expected interval (REQ-038) — flagged, not hidden. |
| Stale | "Data may be stale" banner (REQ-028). |

**Navigation context:** Top of sidebar (ungrouped, daily entry point).
**Related flows:** FLOW-002 (drill), FLOW-003 (promote).

### SCR-007: Service List

**Purpose:** Browse logical services (CA / DNS / DHCP) (REQ-013).
**Primary user goal:** Find a service and open its detail.
**Entry points:** Inventory › Services; dashboard "failed/degraded services" card (`?health=degraded`).

**Content areas:**
- Toolbar: filter by service type (CA/DNS/DHCP), health; search by name.
- Table columns: Service Name, Type, Host/Endpoint, Health (icon+label+color), Last Seen.

**Key interactions:** click row → Service Detail (SCR-008); filter/sort URL-encoded.
**Data shown:** service identity, type, endpoint, health, last-seen.
**States:** Family A shared states.
**Navigation context:** Inventory group.
**Related flows:** FLOW-001 (drill target).

### SCR-009: Security Posture

**Purpose:** Cross-fleet compliance view: firewall, AV, TPM, Secure Boot, pending security updates (REQ-015).
**Primary user goal:** Find non-compliant devices.
**Entry points:** Posture › Security; dashboard "security issues" card (`?compliant=false`).

**Content areas:**
- Toolbar: filter by compliance (compliant/non-compliant), by specific control (missing firewall / AV / TPM / Secure Boot), zone; search.
- Table columns: Hostname, Firewall (on/off), AV (present/name), TPM (present), Secure Boot (on/off), SELinux status, Pending Security Updates count. Each control cell uses icon+label+color (REQ-015; no color alone).

**Key interactions:** click row → Device Detail Security tab (SCR-006); filter to a specific failing control.
**Data shown:** per-device posture controls (REQ-015 / glossary "Posture").
**States:** Family A shared states; empty = "All devices compliant" positive empty state when filter=non-compliant returns none.
**Navigation context:** Posture group.
**Related flows:** FLOW-001.

### SCR-010: Certificate Inventory

**Purpose:** Fleet-wide X.509 certificate inventory with expiry timeline and CA correlation (REQ-016).
**Primary user goal:** Find expiring/expired certs during rotation.
**Entry points:** Posture › Certificates; dashboard "expiring certificates" card (`?status=expiring`).

**Content areas:**
- Toolbar: filter by status (valid/expiring≤30d/expired), issuing CA, device; search by subject.
- Optional expiry-timeline summary strip (count buckets: expired / ≤7d / ≤30d / valid).
- Table columns: Subject, Issuer (CA), Device/Host, Expiry date, Days Remaining, Status (icon+label+color), CA correlation (link to the issuing Step CA service where known).

**Key interactions:** click row → Device Detail Certificates tab; click CA → Service Detail (SCR-008) for the issuing step-ca; filter to expiring.
**Data shown:** cert subject/issuer/expiry/status; host; CA linkage.
**States:** Family A shared states; long subjects truncate with tooltip/expander.
**Navigation context:** Posture group.
**Related flows:** FLOW-001.

### SCR-011: Patch Status

**Purpose:** Devices with pending updates / security updates / reboot-required (REQ-019).
**Primary user goal:** See what needs patching/rebooting.
**Entry points:** Posture › Patches; dashboard "pending security updates" (`?type=security`) and "reboot required" (`?reboot=required`) cards.

**Content areas:**
- Toolbar: filter by pending-security, reboot-required, zone; search.
- Table columns: Hostname, Pending Updates, Security Updates, Reboot Required (yes/no icon+label), Last Check Time.

**Key interactions:** click row → Device Detail Updates tab; filter to security/reboot.
**Data shown:** REQ-019 update counts + reboot flag + last-check.
**States:** Family A shared states.
**Navigation context:** Posture group.
**Related flows:** FLOW-001.

### SCR-012: Storage Health

**Purpose:** Disk SMART health + filesystem capacity across the fleet (REQ-020).
**Primary user goal:** Find degraded disks and near-full filesystems.
**Entry points:** Posture › Storage; dashboard "degraded disk SMART" card (`?smart=degraded`).

**Content areas:**
- Toolbar: filter by SMART status (healthy/degraded/failing), filesystem usage threshold (e.g., ≥90%), zone; search.
- Table columns: Hostname, Disk (serial/type), SMART Status (icon+label+color), Filesystem mount, Total, Used, Free, Usage% (with a bar). Two related concerns (disk health + FS capacity) in one view per REQ-020.

**Key interactions:** click row → Device Detail Disks/Filesystems tabs; filter to degraded.
**Data shown:** disk SMART + filesystem capacity (REQ-020).
**States:** Family A shared states; usage% bar visualizes capacity.
**Navigation context:** Posture group.
**Related flows:** FLOW-001.

### SCR-013: Open Ports

**Purpose:** Cross-device listening-port search to audit network exposure (REQ-017).
**Primary user goal:** Find what's listening on a given port anywhere in the fleet.
**Entry points:** Network › Open Ports.

**Content areas:**
- Toolbar: search by port number; filter by protocol (TCP/UDP), process name, zone.
- Table columns: Hostname, Protocol, Port, Process Name, PID. Sortable by port.

**Key interactions:** search a port → filtered rows across all devices; click row → Device Detail Ports tab.
**Data shown:** REQ-017 cross-device port rows.
**States:** Family A shared states; overflow common (many ports across many hosts) — keyset paginate.
**Navigation context:** Network group.
**Related flows:** FLOW-002.

### SCR-014: Container Fleet

**Purpose:** Cross-device Docker container view (REQ-018).
**Primary user goal:** Find non-running / unhealthy containers.
**Entry points:** Network › Containers; dashboard "containers not running" card (`?state=not-running`).

**Content areas:**
- Toolbar: filter by state (running/stopped/paused), health, image; search by name.
- Table columns: Hostname, Container Name, Image, State (icon+label+color), Health, CPU%, Memory.

**Key interactions:** click row → Device Detail Containers tab; filter to not-running.
**Data shown:** REQ-018 container rows.
**States:** Family A shared states.
**Navigation context:** Network group.
**Related flows:** FLOW-001.

### SCR-015: ARP / Network Cross-Reference

**Purpose:** IP → MAC → device lookup across the fleet (REQ-021).
**Primary user goal:** Trace a known IP or MAC back to a device.
**Entry points:** Network › ARP / Network.

**Content areas:**
- Toolbar: search by IP or MAC; filter by observing agent/interface.
- Table columns: IP, MAC, OUI Vendor, Observed-by (agent/interface), Resolved Device (link to Device Detail if matched), Last Seen.

**Key interactions:** search an IP/MAC → matching ARP rows; click resolved device → Device Detail (SCR-006).
**Data shown:** REQ-021 ARP/neighbor cross-reference.
**States:** Family A shared states; unresolved entries (MAC with no matched device) shown with "—" resolved device + an option to view as a discovered host if one exists.
**Navigation context:** Network group.
**Related flows:** FLOW-002 (resolve to device).

### SCR-016: Change Feed

**Purpose:** Time-ordered audit trail of fact value changes from facts_history (REQ-022, glossary "Change Feed").
**Primary user goal:** See what changed and when.
**Entry points:** Audit › Change Feed.

**Content areas:**
- Toolbar: filter by entity (device/service), attribute path, time range; search.
- Table columns: Timestamp, Entity (host/service link), Attribute (e.g., `Interface[eth0].Speed`), Old Value, New Value, Source.

**Key interactions:** click entity → its detail page; filter by device or attribute path; time-range scrub.
**Data shown:** REQ-022 change records (attribute path, old/new, source, time).
**States:** Family A shared states; high-volume — keyset paginate by timestamp; default to recent window.
**Navigation context:** Audit group.
**Related flows:** FLOW-002.

### SCR-017: Sessions

**Purpose:** Cross-device active login sessions (REQ-023).
**Primary user goal:** See who is logged in where.
**Entry points:** Audit › Sessions.

**Content areas:**
- Toolbar: filter by user, host; search.
- Table columns: Hostname, User, TTY, From (IP), Login Time.

**Key interactions:** click host → Device Detail Sessions tab.
**Data shown:** REQ-023 session rows.
**States:** Family A shared states.
**Navigation context:** Audit group.
**Related flows:** FLOW-002.

### SCR-018: Accounts

**Purpose:** Cross-device local user accounts (REQ-023).
**Primary user goal:** See which accounts exist across the fleet.
**Entry points:** Audit › Accounts.

**Content areas:**
- Toolbar: filter by host, group; search by username.
- Table columns: Hostname, Username, Groups, Last Login.

**Key interactions:** click host → Device Detail Users tab.
**Data shown:** REQ-023 account rows.
**States:** Family A shared states.
**Navigation context:** Audit group.
**Related flows:** FLOW-002.

---

## Entity Detail screens

### SCR-006: Device Detail ⟶ highest density
**Purpose:** All available information about one device, organized into sections (REQ-012). Read from projection tables. Handles both managed and discovered devices (REQ-030, REQ-035, REQ-038).
**Primary user goal:** Read everything known about this host — to diagnose, investigate, explore, or understand it. Not a single-task screen; the operator may satisfy curiosity, trace a problem, or make a promotion decision.
**Entry points:** Device List, All Hosts, any reporting table row, ARP resolved device.

**Content areas:**
- **Persistent identity header** (above the tabs): hostname, device UUID, vendor, model, device type, and **management status badge** (managed/discovered, prominent — REQ-030). Header uses a two-column kv grid to fill horizontal width and avoid stacking all fields in a single column.
  - **Managed:** kv shows UUID, OS, Vendor, Kernel, Model, collecting Agent (name + online/offline), Type, Timezone, Boot Time, Uptime, Last Seen. System-level fields (Timezone, Boot Time, Uptime) live in the header — no separate System tab.
  - **Discovered:** kv shows UUID, Vendor, Model, Type (Zone when available). Below the kv, a **confidence summary** line: '● Seen by N sources · last Xs ago' (REQ-038). Action buttons (Promote, Merge) on the right side of the header — discovered + Admin role only (REQ-036).
- **Tabbed body** over the sections (REQ-012). Tabs with no data are suppressed entirely — not shown empty (REQ-012/030 — documented exception to the global empty-state pattern). Multi-row section tabs show an item count chip.
  - **Managed default tab:** Hardware.
  - **Discovered default tab:** Discovery Sources.
  - **Available tabs:** Hardware, Interfaces, Disks, Filesystems, Containers, Ports, Discovery Sources. (System tab eliminated — its fields moved into the identity header. Security, Updates, Users, Sessions, ARP, Routes, Battery, Certificates: deferred — shown only when data exists.)
- **Per-section freshness:** Each tab panel opens with a small 'Updated X ago' line derived from the section's most recent collected_at / updated_at. This is critical for managed devices where data age matters.
- **Discovery Sources tab** (all devices, but primary for discovered): three sub-sections:
  1. *Identity Provenance* — table of identity fields (Hostname, IP, MAC, OUI Vendor) with canonical value, winning source tag, and 'show all sources' toggle revealing lower-precedence competing values (REQ-035). Precedence: agent-direct > lldp > mdns > nbns > arp/dhcp.
  2. *Discovery Sources* — per-source observation stats: source type, agent, first seen, last seen, observation count (REQ-038).
  3. *Raw Fingerprints* — collapsible; shows individual identity assertions from the fingerprint table. Collapsed by default to avoid noise; available for deep inspection.

**Key interactions:**
- Click a tab → jump to that section; only populated sections are reachable.
- Toggle 'show all sources' on identity fields → reveal lower-precedence values with source attribution; default collapsed.
- Promote (discovered, admin) → Promotion drawer (SCR-022), echoing confidence summary at the irreversible step (F-08).
- Merge (admin) → Merge drawer (REQ-053).
- Tab count chip shows item count for multi-row sections at a glance.

**Data shown:** full REQ-012 section data; for discovered, per-source provenance (REQ-035/038). Raw fingerprints available on demand.

**Implementation gaps (as of 2026-06-06 review):**
| Gap | Severity | Description |
|-----|----------|-------------|
| GAP-DD-1 | High | Header uses single-column kv; wastes horizontal space; missing Agent (managed) and confidence summary (discovered). |
| GAP-DD-2 | High | All 8 tabs always rendered regardless of data — empty tabs are noise and obscure real content. |
| GAP-DD-3 | High | Discovery Sources tab shows flat fingerprint dump only — no Identity Provenance sub-section, no per-source observation stats, no provenance toggle. |
| GAP-DD-4 | Medium | No per-section last-updated timestamps — operator cannot tell how fresh the data is. |
| GAP-DD-5 | Medium | No Promote or Merge actions implemented — discovered device has no action surface. |
| GAP-DD-6 | Low | System tab shows Hostname, duplicating the identity header. Resolved: System tab eliminated; fields moved to header. |
| GAP-DD-7 | Low | Discovery Sources tab is the default for all devices — should be Hardware for managed devices (System tab removed). |

**States:**
| State | Description |
|-------|-------------|
| Populated (managed) | Identity header with agent, OS, kernel, timezone, boot time, uptime; Hardware default tab; all populated tabs visible with counts; per-section timestamps. |
| Populated (discovered) | Identity header with confidence summary + action buttons; Discovery Sources default tab; only tabs with passive data visible. |
| Sparse (lone-ARP discovered) | Only identity + Discovery Sources render — sparse by design; provenance section shows ARP-only with low-confidence note. |
| Loading | Identity header skeleton + tab skeletons. |
| Error (per-section) | A failed section's tab shows an inline error + retry; all other tabs still work (REQ-028). |
| Stale | Per-section staleness flag on its last-updated line; page-level 'data may be stale' banner if overall stale (REQ-028). |
| Promotion pending | Mid-promotion shows 'promotion pending — awaiting first collection' indicator; passive data still visible. |

**Navigation context:** Drill target from many lists; back returns to origin with filter state intact.
**Related flows:** FLOW-002 (investigate), FLOW-003 (promote).
**Revised mockup:** planning/ux/mockups/device-detail.html (updated 2026-06-06, iteration 3).

### SCR-008: Service Detail

**Purpose:** All information about one logical service (CA / DNS / DHCP) (REQ-014). Mirrors Device Detail's shape for peer consistency.
**Primary user goal:** Read a service's status and its type-specific data.
**Entry points:** Service List; Certificate Inventory CA correlation.

**Content areas:**
- Persistent identity header: service name, type (CA/DNS/DHCP), host/endpoint, health, service UUID, last-updated.
- Sectioned body (type-specific; no-data sections hidden):
  - **CA (step-ca):** issued certificates table (subject, expiry, status), CA config summary.
  - **DNS (Technitium):** zones and records.
  - **DHCP (Technitium):** active leases (IP, MAC, hostname, expiry).

**Key interactions:** click an issued cert → Certificate Inventory / device cert context; click a DHCP lease MAC → ARP/device resolution.
**Data shown:** REQ-014 service-type data.

**States:**
| State | Description |
|-------|-------------|
| Populated | Identity header + type-specific sections. |
| Empty section | Sections with no data hidden (consistent with Device Detail). |
| Loading | Header + section skeletons. |
| Error (per-section) | Inline error + retry per section (REQ-028). |
| Stale | Staleness flags as Device Detail. |

**Navigation context:** Drill target from Service List / Cert Inventory.
**Related flows:** FLOW-001.

---

## Family B: Admin configuration

Moderate density (lower frequency, higher consequence). Edits use explicit Save; secrets write-only; deletes confirm.

### SCR-019: Agent List

**Purpose:** List registered agents with liveness (REQ-003, REQ-004). Admin only.
**Primary user goal:** Find an agent to configure / see which are offline.
**Entry points:** Admin › Agents; dashboard "stale agents" card (`?heartbeat=stale`).

**Content areas:**
- Toolbar: filter by status (online/offline/stale), zone; search by name.
- Table columns: Agent Name, Zone, Last Heartbeat, Status (online/offline/stale — icon+label+color), Targets count, Collectors enabled.
- Page action: "Add Agent" (registration) — admin.

**Key interactions:** click row → Agent Detail (SCR-020); Add Agent → registration flow.
**Data shown:** agent identity, heartbeat, status, target/collector counts (REQ-003/004).
**States:** Family A shared states (it is a table), plus empty = "No agents registered — add your first agent" CTA.
**Navigation context:** Admin group (admin only).
**Related flows:** FLOW-004.

### SCR-020: Agent Detail (hub)

**Purpose:** Configure one agent — settings, targets, collectors — as a hub-and-spoke page (REQ-005, REQ-006, REQ-008, REQ-009). Admin only.
**Primary user goal:** Adjust this agent's collection behavior.
**Entry points:** Agent List row.

**Content areas:**
- Identity header: agent name, zone, status, last heartbeat, agent UUID, server URL.
- **Configuration section:** editable Name, Zone, Interval (human-readable duration), MaxConcurrency. Advanced fields (MaxConcurrency, overrides) grouped but visible. Explicit **Save**. Shows effective last-delivered config alongside pending changes (REQ-005).
- **Targets section:** table (endpoint, collector type, credential ref, label, enabled); Add/Edit (modal), Delete (confirm), Discover targets… (candidate picker) (REQ-006). One collector-type dropdown spans both device-style (ssh, snmp, bacnet, modbus, google-wifi) and service-style (technitium-dns, home-assistant) collectors; the endpoint field's helper text/placeholder switches between "bare host/IP" and "URL, including the port" based on the selected type.
- **Collectors section:** list of collectors, each with enable/disable toggle (REQ-008) and an optional per-collector frequency override (placeholder shows inherited agent interval, REQ-009).

**Key interactions:**
- Edit config → Save → persisted + audit-logged + delivered on next poll (REQ-005/027/DEC-001); pending indicator until acknowledged.
- Add/Edit target → modal; submit persists.
- Toggle a collector / set a frequency → persisted.
- Delete a target → confirmation.

**Data shown:** agent settings, target lists, collector roster + state (REQ-005/006/008/009).

**States:**
| State | Description |
|-------|-------------|
| Populated | All sections with current values; pending-vs-delivered config indicator. |
| Empty targets | "No targets configured — add one" within the targets section. |
| Loading | Section skeletons. |
| Saving | Save button busy; on success a toast; on failure a specific inline error, form populated, no partial commit (REQ-028). |
| Agent offline | Config still editable; "changes will be delivered when the agent reconnects" note; pending indicator persists. |
| Save error | Inline error with the specific cause; no partial save (REQ-028). |

**Navigation context:** Admin group; hub for config/targets/collectors.
**Related flows:** FLOW-004.

### SCR-021: Credentials

**Purpose:** Manage the encrypted credentials store (REQ-007). Admin only.
**Primary user goal:** Create / rotate / rename / delete credentials. Secrets write-only.
**Entry points:** Admin › Credentials.

**Content areas:**
- Toolbar: search by label; filter by type.
- Table columns: Label, Type, Created At, Last Used At, Referenced By (count). **No secret values anywhere.**
- Page action: "Add Credential" (modal).
- Per-row actions: Rename, Rotate, Delete.

**Key interactions:**
- Add → modal: Label + Type (select) → type-appropriate secret field(s) revealed → submit (encrypted at rest, REQ-007/DEC-002, audit-logged).
- Rename → modal (label only, no secret).
- Rotate → modal (enter NEW secret; existing never shown; references preserved).
- Delete → if Referenced By > 0, confirmation lists referencing targets and requires confirm (REQ-007); else simple delete confirm.

**Data shown:** credential metadata only (label, type, created/last-used, reference count) — never secret material (REQ-007).

**States:**
| State | Description |
|-------|-------------|
| Populated | Credential table. |
| Empty | "No credentials yet — add one." |
| Loading | Table skeleton. |
| Modal error | Specific inline error; form populated; no partial commit (REQ-028). |
| Delete-referenced | Confirmation dialog listing the dependent targets (REQ-007). |

**Navigation context:** Admin group.
**Related flows:** FLOW-005.

### SCR-022: Promotion Modal

**Purpose:** Promote a discovered device to managed (REQ-036). Admin only. Overlay launched from SCR-004 / SCR-006.
**Primary user goal:** Assign an agent + credential + target and start direct collection.
**Entry points:** Promote button (All Hosts row / Device Detail header).

**Content areas:**
- Step A — Select agent (select of registered agents).
- Step B — Credential (select existing, or "create new" inline; type-appropriate; write-only secret) (REQ-007).
- Step C — Confirm target IP/hostname (pre-filled from discovered facts).
- Final confirm — restates consequence: "Agent X will begin SSH collection against host Y using credential Z. This creates a remote target and starts live collection. Existing passive data is kept." (Acknowledges the non-reversible nature: managed→discovered is not a normal transition.)

**Key interactions:**
- Confirm → creates RemoteTarget (REQ-006) bound to agent+credential; audit-logged (REQ-027); toast "Promotion queued"; modal closes; row/page shows "promotion pending."
- Cancel → nothing created; returns to origin with state intact.

**Data shown:** agent list; credential labels/types; discovered device IP/hostname for pre-fill.

**States:**
| State | Description |
|-------|-------------|
| Open | Modal with the three inputs + final confirm. |
| Validation | Missing agent/credential/target → inline validation before confirm. |
| Submitting | Confirm busy; on success toast + close. |
| Save error | Specific inline error; no partial RemoteTarget (REQ-028). |
| Post-confirm (on page) | Origin host shows "promotion pending"; on connection failure later, host reverts to discovered + Admin-area notification (REQ-036). |

**Navigation context:** Overlay; does not change the underlying screen.
**Related flows:** FLOW-003.

---

## System screens

### SCR-023: Error Page (whole-page failure)

**Purpose:** Graceful whole-app failure (e.g., PostgreSQL unreachable) without leaking internals (REQ-028).
**Primary user goal:** Understand the app is down and what to do; never see a stack trace.
**Entry points:** System-triggered on unrecoverable backend failure.

**Content areas:** clear human message ("The Facts Server can't reach its database right now."), a retry action, and a note that the error was logged server-side. No stack trace, no DB error text, no SQL (REQ-028).
**States:** single state (error). Retry attempts to reload.
**Navigation context:** System.
**Related flows:** referenced by error-states (Phase 3).

### SCR-024: Not Authorized (403)

**Purpose:** A Read-Only Viewer hitting an Admin-only URL directly gets a clear 403, not a broken page or a leak (REQ-002).
**Primary user goal:** Understand they lack access.
**Entry points:** Viewer navigates/links to an Admin route.

**Content areas:** "You don't have access to this area" + a link back to the Dashboard. The Admin nav is already hidden for viewers; this guards direct URL access.
**States:** single state.
**Navigation context:** System.
**Related flows:** RBAC enforcement (REQ-002).
