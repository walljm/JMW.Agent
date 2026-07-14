---
agent: sdev-ux
date: 2026-07-14
iteration: 3
revision_id: 10
status: draft
---

# Information Architecture — JMW Agent Facts Server

## Navigation Model

**Iteration 3 rewrite.** As the report surface grew (Subnets, Hardware, Interfaces, Components,
Terrain's DHCP/DNS sub-views, a Fleet Overview split from Agents, and three separate admin
clusters), the grouped-sidebar-of-links model from iteration 2 had drifted to **21 nav links for
an admin, 13 for a viewer** across 7 groups — several of them (Audit had exactly one member;
the group named "Audit" and the admin page "Audit Log" collided in name) had stopped doing real
grouping work. This iteration collapses same-shape peer reports into **hub pages with an in-page
tab bar**, a pattern already proven in this codebase (Terrain has tabbed DHCP Leases/DNS Records
sub-views since before this rewrite) rather than one sidebar link per report.

A **role-aware, flat left sidebar** is the primary navigation, persistent on every authenticated
screen. Each link is either a direct destination (Dashboard, All Hosts, Services) or a hub whose
own page renders a shared tab bar (`_HubTabs.cshtml`) across its member reports. Tabs are plain
links to each member's own URL — filter state, pagination, and route stay exactly as they were;
only the *entry point* consolidates. This keeps every report bookmarkable and keeps the Dashboard's
card→URL contract (see the Filter/URL Convention table below) unbroken, since no report's own route
changed.

Rationale for keeping a persistent sidebar (unchanged from iteration 2): a monitoring tool is used
in long sessions with frequent section switching, and vertical nav space is cheaper than horizontal
— horizontal space stays reserved for the dense data tables that are the point of the app.

### Two renderings (RBAC, REQ-002)

The Admin block is **omitted entirely** (not shown-and-disabled) for the Read-Only Viewer.

**Network Operator (Admin role) sees:**

```
◆ JMW Discovery                 ← product wordmark / home link
─────────────────────────────
  Dashboard                     ← Fleet Dashboard (landing)
  All Hosts                     ← unified managed + discovered, primary drill-down root
  Services                      ← DNS/Home Assistant service entities

  Inventory                     ← hub: Ports · Containers · Storage · Hardware · Interfaces · Components
  Network                       ← hub: Subnets · ARP Table · Terrain · DHCP Leases · DNS Records

  Fleet                         ← hub: Overview · Agents            (admin only)
  System                        ← hub: Credentials · Users · Settings (admin only)
  Data                          ← hub: Conflicts · OUI Database · Activity Log (admin only)
─────────────────────────────
  [user]  ·  Sign out           ← session footer
```

**Read-Only Viewer sees the same, minus Fleet/System/Data.** Dashboard, All Hosts, Services,
Inventory, and Network are reporting and visible to both roles (read-only by DEC-005).

### Why All Hosts and Services stay top-level while the rest of Inventory tabs

**All Hosts** is the unified managed+discovered device list and the primary device drill-down
root (REQ-037) — the single highest-frequency screen after Dashboard. **Services** is the other
entity-root list (drill to Service Detail). Both are *entity lists that a device/service identity
resolves through*, not cross-cutting attribute slices, so they keep direct sidebar links exactly
like Dashboard. The remaining six Inventory reports (Ports, Containers, Storage, Hardware,
Interfaces, Components) are all "what is this host running/made of" slices *across* the fleet —
structurally homogeneous peer reports, not identity roots — so they collapse into one **Inventory**
hub with a shared tab bar. Landing tab: Ports (first in the set).

### Why Network flattens Terrain's own sub-tabs into the hub

Network was already the smallest group (3 links), and Terrain already tabbed its own DHCP/DNS
sub-views — a nested tab-inside-a-group pattern. Rather than leave that nesting, DHCP Leases and
DNS Records are promoted to flat siblings of Subnets/ARP Table/Terrain inside one 5-tab **Network**
hub. Landing tab: Subnets.

### Why Fleet/System/Data stay three separate hubs, not one Admin hub

Fleet (Overview, Agents), System (Credentials, Users, Settings), and Data (Conflicts, OUI
Database, Activity Log) each collapse to one hub link, but are **kept as three hubs rather than
merged into one 8-tab Admin hub** — they are different concerns (fleet health, mutating
configuration, forensic/reference data) and 8 tabs in one bar is cramped. This preserves the
grouping semantics of the old three admin nav groups while still cutting 8 links down to 3.

### Change Feed: demoted, not removed

Change Feed (the raw `facts_history` diff trail) is **no longer a standalone nav destination or
top-level report.** Individual facts change far too fast for a fleet-wide firehose of every raw
diff to carry signal — an operator scanning it can't tell a heartbeat/uptime tick from a state
change that actually matters. Two replacements:

- **Device Detail → "History →"** link (next to "Merge into…" in the page-head) to
  `/changes?deviceId={id}` — the existing per-device filter on the `/changes` route, now the
  primary way to answer "what changed on this host."
- **Dashboard's existing "Recent changes" panel** stays as the fleet-wide glanceable view.

**Deferred (flagged for a follow-up pass, not built in this iteration):** curating `/changes`
and the Dashboard panel to a significant-events-only feed (state transitions, new device, cert/
service health changes) instead of every raw fact diff. That requires a product decision on what
counts as "significant" per fact type before any SQL/aggregate work — a separate design pass, not
a nav change. The `/changes` route and its full raw table are left working as before in the
meantime; they are simply no longer advertised in the sidebar.

### Why config/targets/collectors are not top-level nav items

REQ-005 (config), REQ-006 (targets), REQ-008 (enable/disable), REQ-009 (frequency) are all scoped *per agent*. Surfacing them as global nav items would force the operator to pick an agent first anyway. Instead, Agent Detail is a hub-and-spoke page (decided in usability design) containing Configuration, Targets, and Collectors sections. Targets is one section covering both device-style (SSH/SNMP/BACnet/Modbus/Google Wifi) and service-style (Technitium DNS/Home Assistant) collectors — one table, one add/edit form, distinguished only by a Collector Type field. Credentials, by contrast, ARE fleet-global (one credential is referenced by many targets, REQ-007), so Credentials is its own System-hub tab.

## Screen Inventory

Sequential screen IDs (used in `screen-specs.md`). As-built, iteration 3: 24 originally-planned
screens, minus 3 that were never built (data dropped before shipping — see "Fleet Dashboard
panels" below) plus 2 never built (Sessions/Accounts, no `agents`/`sessions` cross-fleet view was
ever added to Audit), plus 9 screens added since iteration 2 that iteration 2's doc never caught
up to (Subnets, Hardware, Interfaces, Components, DHCP Leases, DNS Records, Fleet Overview split
from Agents, Settings, OUI Database).

| ID | Screen | Nav location | Role | Primary REQ |
|----|--------|--------------|------|-------------|
| SCR-001 | Login | (unauthenticated) | both | REQ-001 |
| SCR-002 | First-Run Bootstrap | (unauthenticated, one-time) | admin | REQ-001 / DEC-003 |
| SCR-003 | Fleet Dashboard | Dashboard | both | REQ-010 |
| SCR-004 | All Hosts | All Hosts (top-level) | both (Promote: admin) | REQ-037 |
| SCR-005 | Device List | — (superseded by SCR-004 All Hosts) | both | REQ-011 (superseded) |
| SCR-006 | Device Detail | (drill from list/All Hosts) | both (Promote: admin) | REQ-012, REQ-030, REQ-035, REQ-038 |
| SCR-007 | Service List | Services (top-level) | both | REQ-013 |
| SCR-008 | Service Detail | (drill from service list) | both | REQ-014 |
| SCR-009 | Security Posture | — not built; `proj_security` dropped (migrations 0027–0037) before shipping | both | REQ-015 (not built) |
| SCR-010 | Certificate Inventory | — not built; `proj_device_certs` dropped before shipping | both | REQ-016 (not built) |
| SCR-011 | Patch Status | — not built; `proj_updates` dropped before shipping | both | REQ-019 (not built) |
| SCR-012 | Storage Health | Inventory hub › Storage | both | REQ-020 |
| SCR-013 | Open Ports | Inventory hub › Ports | both | REQ-017 |
| SCR-014 | Container Fleet | Inventory hub › Containers | both | REQ-018 |
| SCR-015 | ARP Table | Network hub › ARP Table | both | REQ-021 |
| SCR-016 | Change Feed | (no nav link — reached via Device Detail "History →" or the Dashboard recent-changes panel; see "Change Feed: demoted, not removed") | both | REQ-022 |
| SCR-017 | Sessions | — not built | both | REQ-023 (not built) |
| SCR-018 | Accounts | — not built | both | REQ-023 (not built) |
| SCR-019 | Agent List | Fleet hub › Agents | admin | REQ-003, REQ-004 |
| SCR-020 | Agent Detail (hub: config, targets, collectors) | (drill from Agent List) | admin | REQ-005, REQ-006, REQ-008, REQ-009 |
| SCR-021 | Credentials | System hub › Credentials | admin | REQ-007 |
| SCR-022 | Promotion Modal | (overlay from SCR-004 / SCR-006) | admin | REQ-036 |
| SCR-023 | Error Page (whole-page failure) | (system) | both | REQ-028 |
| SCR-024 | Not Authorized (403 for viewer hitting admin URL) | (system) | both | REQ-002 |
| SCR-025 | Subnets | Network hub › Subnets | both | REQ-021 |
| SCR-026 | Hardware | Inventory hub › Hardware | both | REQ-017-adjacent (fleet-wide hardware inventory slice) |
| SCR-027 | Interfaces | Inventory hub › Interfaces | both | REQ-017-adjacent |
| SCR-028 | Hardware Components | Inventory hub › Components | both | REQ-017-adjacent |
| SCR-029 | Network Terrain | Network hub › Terrain | both | REQ-021-adjacent |
| SCR-030 | DHCP Leases | Network hub › DHCP Leases | both | REQ-021-adjacent |
| SCR-031 | DNS Records | Network hub › DNS Records | both | REQ-021-adjacent |
| SCR-032 | Fleet Overview | Fleet hub › Overview | admin | REQ-003-adjacent (fleet health rollup) |
| SCR-033 | Settings | System hub › Settings | admin | (agent liveness thresholds, data retention) |
| SCR-034 | Users | System hub › Users | admin | (role management) |
| SCR-035 | Fingerprint Conflicts | Data hub › Conflicts | admin | REQ-036-adjacent |
| SCR-036 | OUI Database | Data hub › OUI Database | admin | (vendor-lookup data admin) |
| SCR-037 | Activity Log | Data hub › Audit Log | admin | REQ-023-adjacent |

## Labeling Decisions

Labels use the operator's vocabulary from `glossary.md`, not developer/schema terms. Specific decisions:

- **"All Hosts"** not "All Devices" — REQ-037 names it All Hosts; "host" is the colloquial network-operator term that comfortably covers both managed and discovered boxes. ("Device" is reserved for the data-model entity; there is no separate "Devices" nav destination — All Hosts *is* the device inventory list.)
- **"Managed" / "Discovered"** — the management-status values verbatim from REQ-030. Never softened (e.g., not "Active"/"Seen"). The operator's word for trust level.
- **"Discovery Sources"** with the literal protocol tags **ARP, mDNS, LLDP, NBNS, DHCP** — expert vocabulary preserved; no friendly synonyms (REQ-037/038, complex-domain obligation).
- **"Posture"** — glossary term for the firewall/AV/TPM/SecureBoot/updates aggregate (REQ-015). The Security/Certificates/Patches screens that would have surfaced it were never built (see Screen Inventory); the term survives in the glossary for whenever a facts_history-backed posture pass gets built.
- **"Heartbeat" / "Stale"** for agent liveness — glossary terms.
- **"Promote to Managed"** — REQ-036's exact action label.
- **"Zone"** for the grouping label — glossary term, not "site" or "location."
- **"Collector"** (e.g., HardwareCollector) — glossary term; collectors are listed by their real names on Agent Detail.
- Hub nav labels (Inventory, Network, Fleet, System, Data) are organizational, not domain terms — kept short as scan anchors, same role the old sidebar group headers played.

## Filter / URL Convention (drives the dashboard-card → filtered-view contract)

REQ-010 requires every dashboard summary card to link to "the relevant reporting page (or a filtered view)." To make this concrete and consistent, reporting screens encode filter state in the URL query string, and dashboard cards link to the matching pre-filtered URL. This convention is binding for Phase 3 clickable flows.

| Dashboard card | Destination (pre-filtered URL) |
|----------------|-------------------------------|
| Offline devices | `/all-hosts?status=offline` |
| Stale agents | `/admin/agents?heartbeat=stale` |
| Pending security updates | `/patches?type=security` |
| Reboot required | `/patches?reboot=required` |
| Degraded disk SMART | `/storage?smart=degraded` |
| Expiring certificates (≤30d) | `/certs?status=expiring` |
| Failed/degraded services | `/services?health=degraded` |
| Containers not running | `/containers?state=not-running` |
| Security issues (missing fw/av/tpm/sb) | `/security?compliant=false` |
| Filesystems near full (≥90%) | `/storage?fs=full` |
| Fingerprint conflicts (Admin-only) | `/admin/conflicts` |
| Managed / Discovered device counts (Total-devices tile caption) | `/all-hosts?status=managed` / `/all-hosts?status=discovered` |
| Services tile | Services list (SCR-007) |
| Reporting (24h) / quiet (Reporting tile caption) | `/all-hosts?status=reporting` / `/all-hosts?status=stale` |
| Zones tile | `/admin/agents` |
| Pending agents (Admin-only) | `/admin/agents?status=pending` |
| New devices (first seen recently) | `/all-hosts?added=recent` |
| Not seen recently (stale devices) | `/all-hosts?status=stale` |
| Recent fact changes | `/changes` (existing `GET /api/v1/report/changes`) |
| Composition — by vendor / os / kind | `/all-hosts?vendor=…` / `?os=…` / `?kind=…` |

General rules:
- Filter dimensions are query params (`?status=`, `?source=`, `?vendor=`, `?q=` for free text).
- Filter state is restored from the URL on load and updated via the URL when filters change (URL is the source of truth for navigation state).
- Pagination is keyset/cursor-based, carried as an opaque `?after=` cursor param (never `?page=N` / offset).
- These params are domain-oriented (`status=expiring`), not schema-oriented (no column names or table names leak into the URL).

## Fleet Dashboard panels (SCR-003 redesign)

The Fleet Dashboard was redesigned (iteration 2, dashboard-redesign pass) from a flat 9-tile grid
into a grouped, intent-organized layout. The old grid mixed three unrelated concerns (inventory
totals, one data-quality metric, and two placeholder tiles hardcoded to 0), led with a vanity total,
and gave every tile equal weight. The redesign groups content **by operator intent** and orders the
groups **attention-first**. Full panel-by-panel detail is in `screen-specs.md` (SCR-003).

### Reading order and its justification (attention-first, not totals-first)

Panels are ordered top→bottom by *actionability*:

0. **Trust banner** (conditional) — stale-data / agents-offline warning.
1. **Needs attention** (hero, full width) — posture + data-quality rollup.
2. **Fleet health** (2-col) — Agents · Collection. **All agent counts (total/approved/pending + online/stale/offline) live here** — deliberately kept out of the Network totals to avoid duplication.
3. **Recent activity** — Not seen recently · New devices (two lists side by side, "Not seen" first as the more actionable), then **Recent changes full-width underneath**.
4. **Network** (bottom) — **four headline tiles** (Total devices = Managed+Discovered · Services by
   type · Reporting-24h = reporting+quiet · Zones) each with a subtle part-to-whole bar (neutral splits
   in the indigo family, the reporting health-ratio in `--ok`/`--warn`; colour as reinforcement only) ·
   composition bars · change-trend sparkline.

**Why attention-first over totals-first:** SCR-003 is the highest-frequency screen (every login,
per FLOW-001) and the operator's job there is *triage*, not admiring inventory. Placing the scariest,
most actionable content in the top-left first-fixation zone minimizes time-to-problem; reassuring
vanity counts (device/agent totals) anchor the bottom, where the eye lands last, because they rarely
change behavior. The **trust banner precedes even Needs Attention** because a false "all clear" is
more dangerous than a visible warning — if collection is degraded or agents are offline, the operator
must know the feed is unreliable *before* trusting the panels below it. This inverts the old grid,
which led with "Total Devices" and aggregated nothing actionable.

### Per-panel htmx refresh strategy

Each panel is an independent htmx fragment (`hx-get="/dashboard?fragment=<panel>"`, `hx-swap="outerHTML"`)
so a slow or failing panel never blocks the others, and volatile panels poll faster than slow-moving
ones. Cadence by data volatility: **Recent activity 30s**, **Needs attention 60s**, **Fleet health 60s**,
**Network 5m**. The fragment boundary is the *failure-isolation* unit: each `.dash-panel` is its own
fragment (`fragment=attention|agents|collection|not-seen|new-devices|changes|totals|composition|trend`),
so a slow or failing panel never blocks a sibling. Per-panel failure renders `_SectionError` (a `.empty`
"Failed to load this section") inside the affected panel only (REQ-028).

The **trust banner** cannot simply reuse `_StaleIndicator` as-is: that partial renders one hardcoded
string from a `bool`. It must be **extended** to accept a message + severity (or a new partial added)
so it can render the offline-agent count / newest-fact timestamp and switch to `.banner.error` when an
agent is offline. Flagged below as a required backend change.

### New backend aggregates/endpoints the design requires

The existing `GET /api/v1/dashboard/summary` (`DashboardSummary`: device/agent totals + fingerprint
conflicts, 30s cache) covers only panel 4's totals and one Needs-Attention row. The redesign implies
these **new** server-side aggregates (none exist today) — flagged for the downstream implementer; UX
does not implement them:

1. **Agent health rollup** — per-agent heartbeat *age* → derived online / stale / offline (threshold
   = 2× expected interval; never computed today), plus status, version, zone, `passive_discovery_mode`.
   Feeds panel 2a and the trust banner's offline count.
2. **Collection rollup** — latest `agent_cycles` row per agent aggregated fleet-wide: sum `facts_sent`,
   count agents with `error_count > 0`, avg `duration_ms`, **plus an error-count series over the last
   N cycles** for the sparkline (7-day retention). No aggregate over `agent_cycles` exists today.
3. **Stale-device count + list** — devices whose newest `device_fingerprints.last_seen` /
   `proj_systems.updated_at` is older than N days. Replaces the fake "Devices Stale = 0" tile.
4. **New-device count + list** — devices by `device_fingerprints.first_seen` / `devices.created_at`
   within the last N days.
5. **Posture rollups** (Needs Attention counts). **DATA REALITY (as-built):** migrations 0027–0037
   dropped `proj_device_services`, `proj_device_certs`, `proj_updates`, and `proj_security` — that data
   now lives only in append-only `facts_history`, not in queryable projections. Per user decision, the
   posture panel is built from the *surviving* projections and the `facts_history`-only signals are
   deferred (a later facts_history-backed slice). **Built** (`GetPostureSummary.sql` + existing
   `CountConflictsAsync`): bad-SMART disks (`proj_disks.smart_health <> 'PASSED'`); filesystems ≥ 90%
   (`proj_filesystems.used_pct`); containers not running (`proj_containers.state <> 'running'`); failed
   hardware (`proj_hardware_inventory.status NOT IN ('ok','healthy')`); service CA certs expiring ≤30d
   (`proj_service_ca.root_not_after`/`int_not_after`); fingerprint conflicts.
   **Gating (DEC E1, incremental reveal — as-built).** A row is a link only when its drill-in screen
   exists: bad-SMART disks + filesystems → `/storage`; containers → `/containers`; conflicts →
   `/admin/conflicts` (admin-only). Failed hardware and service-CA certs render as NON-linked
   informational rows (count computed, no screen yet). Deferred entirely (data dropped AND no screen):
   failed/degraded device services, device certs, pending updates/reboot-required, security-control gaps.
6. **Composition group-bys** — device counts by `proj_devices.vendor`, by `proj_systems` os_family, and
   by `proj_devices.kind` (top-N + "other").
7. **Change-trend series** — `facts_history` bucketed by day over the recent window for the sparkline
   (plus a 24h change count); the recent-changes list reuses `GET /api/v1/report/changes` (page route `/changes`).
8. **Agent totals** (total / approved / pending) — already produced by the existing
   `GetDashboardSummary`, but they now render in the **Agent Health** fragment, not the Network
   device-totals fragment (which drops the agent columns). No new query, just relocation.
9. **Services total + by-type** — `COUNT(*)` over `services` plus a `GROUP BY services.type`
   (top-N + "other") for the Services headline tile's part-to-whole bar.
10. **Reporting-vs-quiet (24h)** — count of devices whose newest `device_fingerprints.last_seen`
    (or `proj_systems.updated_at`) is within ~24h vs older, for the Reporting tile. This is the
    headline coverage ratio; the >7d **stale** list (aggregate #3) is the drill-in — different
    thresholds, intentionally.
11. **Distinct zone count** — `COUNT(DISTINCT zone)` over `agents` for the Zones tile. Richer
    alternative (implementer's choice): distinct subnets from `proj_dhcp_scopes` / `proj_device_routes`.

**As-built deltas (SCR-003 implementation):**

- Agent liveness (online/stale/offline) is derived from each agent's own `heartbeat_interval_secs`
  (online ≤ 3× interval; offline if `last_heartbeat` NULL or > 1h; else stale) in `GetAgentHealthSummary`
  /`GetAgentHealthList`. Agent totals moved to the Agent Health fragment; the old `/dashboard/summary`
  JSON API and its `GetDashboardSummary.sql` are left unchanged (no breaking API change).
- Zone count **and** zone names come from one query: `GetNetworkSummary` uses `count(distinct zone)` +
  `string_agg(distinct zone)` (a separate single-column query failed source-gen schema validation).
- Composition-by-vendor uses the All Hosts device-maker expression
  `COALESCE(proj_devices.vendor, proj_hardware.system_vendor)`. `proj_devices.vendor` is fanned in by
  `DeviceVendorDerivation` from whichever protocol collector reports one (self-reported hardware,
  BACnet, Modbus, or Google Wifi's own vendor assertion) — the dedicated `proj_bacnet_device` /
  `proj_modbus_device` tables were dropped in the vendor-unification pass; BACnet/Modbus details now
  live in Device Detail fact views instead of dedicated tabs. All three composition bars ship
  **non-linked** (the os/kind/vendor All Hosts filters aren't confirmed wired) — deep-linking is a
  follow-up (see E2 below).
- New indexes (`0039_dashboard_indexes.sql`): `devices(created_at DESC)`, `device_fingerprints(last_seen DESC)`.
- Verified by 6 behavioral integration tests + automatic per-query schema validation (122 tests green).

**Required non-aggregate changes (flagged for the implementer):**

- **Extend `_StaleIndicator`** to accept a message + severity (or add a small trust-banner partial) so
  the dashboard trust banner can show the offline-agent count / newest-fact timestamp and use
  `.banner.error` when an agent is offline. The current partial renders one hardcoded string from a `bool`.
- **Add `os` and `kind` filter dimensions to the All Hosts (SCR-004) toolbar and query.** The Composition
  panel's by-OS-family and by-kind bars deep-link to `/all-hosts?os=…` / `?kind=…`, but SCR-004 today only
  filters by management status, discovery source, and OUI vendor. Until these two dimensions are added,
  those bars land on unfiltered All Hosts. (Vendor deep-links work today.)

Each aggregate should be exposed as (or bundled into) a fragment the Razor page can render per
`?fragment=<panel>`, and should carry its own output cache aligned to the panel's refresh cadence.
Per project DB guidance: prefer `FILTER`-aggregate single scans (as `GetDashboardSummary.sql` already
does) over many subqueries; posture rollups are simple `COUNT … WHERE`/`GROUP BY` over indexed
`proj_*` columns.

## Mental-Model Alignment Summary

The IA mirrors the operator's two work loops:

- **Scan loop:** Dashboard (top of nav) → card → filtered reporting page (Inventory / Network hubs). Consolidating same-shape reports into hubs puts the health/topology lenses where the operator reaches for them without a long flat link list to scan first.
- **Investigate loop:** any list (All Hosts, Services, or any Inventory/Network hub tab) → entity detail. Detail pages share one shape (identity header + sectioned body) regardless of entity type, so the operator learns the pattern once.
- **Host-centric model:** All Hosts is the single device inventory list and leads to Device Detail; managed and discovered are one entity at two fidelities, never split into separate destinations. (An earlier design had a separate "Devices" list under Inventory; it was dropped as redundant with All Hosts — same canonical-device population, no distinct differentiator built.)
- **Configuration as a sub-activity:** kept inside the Fleet/System/Data admin hubs and inside the agent hub, off the main scan/investigate path, because it's infrequent and the operator should not trip over mutating controls during read-only monitoring.
- **Forensic lookup, not a firehose:** "what changed" is now answered per-entity (Device Detail → History) and via a fleet-wide glance (Dashboard recent-changes panel), not by a dedicated nav destination for browsing the raw change log — see "Change Feed: demoted, not removed" above.
