# Presentation Layer

The UI and API surface. Reads derived state from the store. Never calls pipeline functions, never constructs entity state, never runs merge logic. If a view needs computed data, that data must be pre-computed by the Derive stage and queryable from a table or materialized column.

## Principle: Read-Only Queries

```
Pipeline (writes) ──▶ Store ◀── Presentation (reads)
```

Handlers call store query methods that return fully-formed view models. The presentation layer's job is:
1. Authenticate the request.
2. Call a store query method.
3. Render the result (HTML template or JSON).

If a view needs data that requires joining 5 tables and computing aggregates, that logic lives in a store query method — not in the handler. If it's too expensive to compute on every request, it's materialized by the Derive stage.

---

## Views

### Dashboard

**URL:** `/`

**Shows:**
- Agent count (approved / total) with health indicators
- Active alert count by severity
- Recent events (last 24h)
- Network summary (monitored network count, total device count)
- Terrain status (DNS server reachable, query stats)

**Data sources:** Aggregation queries across agents, alert_firings, events, networks, terrain cache.

### Device List

**URL:** `/devices`

**Shows:** All known hardware entities, aggregated across their systems and interfaces.

**Columns:**
- Primary hostname (from highest-priority system in the group)
- All IPs (from all interfaces across all systems on this hardware)
- All MACs (from all interfaces)
- Vendor (OUI of primary interface)
- Kind (hardware form_factor or inferred system type)
- Last seen (MAX across all interfaces)
- Network (derived from primary IP)
- Tags (aggregated from hardware + all children)

**Filters:**
- Network (dropdown)
- Kind (dropdown)
- Tag (dropdown)
- Online/offline (based on last_seen threshold)
- Search (hostname, IP, MAC substring match)

**Grouping:** Each row represents one hardware entity. Multiple interfaces/systems are collapsed into the row with indicators ("+3 IPs", "+2 MACs").

**Sort:** By last_seen (default), hostname, IP, vendor.

### Device Detail

**URL:** `/devices/{id}`

**Shows:** Everything known about one hardware entity and all its children.

**Sections:**
1. **Identity** — Primary hostname, all IPs, all MACs, vendor, kind, first/last seen, confidence indicator for hardware grouping.
2. **Systems** — List of systems on this hardware (bare-metal OS, VMs, containers). Each shows hostname, OS, state, agent link if managed.
3. **Interfaces** — All network attachments. MAC, current IP, type, vendor, network membership, last seen.
4. **Services** — Detected services running on any system (DNS, Docker, web servers, etc.). Shows port, state, observation source.
5. **Disks** — Storage devices on managed systems. Capacity, usage, SMART health, temperature.
6. **Observations** — Timeline of all observations across all interfaces. Who saw what, when, via what method.
7. **Hostname Aliases** — All observed names from all sources. Shows which is canonical and why.
8. **DNS Activity** — Query volume from terrain cross-reference (if available).
9. **Description & Tags** — User-editable notes and labels.

**Actions:**
- Edit notes/tags
- Manual hostname override
- Merge another hardware entity into the current device by interface ID or MAC
- Split incorrectly grouped interfaces
- Archive (hide from active views)

### Agent List

**URL:** `/agents`

**Shows:** All registered agents with health status.

**Columns:** Hostname, OS/arch, status, version, last heartbeat, IP, uptime, CPU/mem sparkline.

**Actions:** Approve pending, deregister, view detail.

### Agent Detail

**URL:** `/agents/{id}`

**Shows:** Full agent state — identity, inventory, metrics, containers, discovery activity.

**Sections:**
1. **Identity** — Hostname, OS, arch, version, registered/approved timestamps, uptime.
2. **Hardware** — Link to the hardware entity this agent runs on. Serial, vendor, model.
3. **Metrics** — CPU, memory, load graphs (time-range selector). Disk usage bars. Interface throughput.
4. **Containers** — Docker containers on this host. Name, image, state, compose project.
5. **Discovery** — Recent sightings from this agent. How many devices discovered, methods used.
6. **Configuration** — Enabled subsystems, collection intervals.

### Container List

**URL:** `/containers`

**Shows:** All tracked containers across all hosts.

**Columns:** Name, image, state, host agent, compose project, network IPs, started/created.

**Filters:** Host agent, state (running/stopped/removed), compose project, search.

### Container Detail

**URL:** `/containers/{agent_id}/{container_id}`

**Shows:** Full container state.

**Sections:** Identity (name, image, ID), lifecycle (state, health, uptime), networking (IPs, MACs, network names), composition (project, service, labels), host link.

### Network List

**URL:** `/networks`

**Shows:** All discovered network segments.

**Columns:** Name, CIDR, gateway, SSID, device count, status, discovered by.

**Filters:** Status (monitored/discovered/ignored).

**Actions:** Set status, rename, view detail.

### Network Detail

**URL:** `/networks/{id}`

**Shows:** All devices on a specific network segment.

**Content:** Filtered device list (same columns as device list) scoped to interfaces with an address in interface_addresses that falls within this network's CIDR. Plus network metadata (gateway, CIDR, SSID, VLAN).

### Alert Rules & Firings

**URL:** `/alerts`

**Shows:** Alert configuration and current state.

**Tabs:**
- **Active** — Open firings (unresolved). Severity, agent, metric, value, duration.
- **Rules** — Configured rules with enable/disable toggles. Edit form.
- **History** — Resolved firings. Duration, resolution time.
- **Channels** — Notification channel config (email, webhook).

### Sources

**URL:** `/sources`

**Shows:** All configured pipeline sources (terrain pollers, future SNMP/Nmap, the implicit agent feed) and their health.

**This is the only place source configuration is created or edited.** There is no file-based source config; the `sources` table is the source of truth (see entity-model.md → Source and infrastructure.md → What Belongs in `server.toml` vs the Database).

**List view columns:** Name, kind, status (green/yellow/red based on `last_success_at` vs `poll_interval_seconds`), last success, last error (truncated), enabled toggle.

**Filters:** Kind, status, enabled-only.

**Actions:** New Source (modal with per-kind form), Edit, Disable/Enable, Delete (soft-delete preserved for audit).

**Per-source detail page (`/sources/{id}`):**
- Header: name, kind, enabled toggle, linked Service (if any)
- Health card: last success, last error, consecutive-error count, current effective interval (accounting for back-off)
- Config form: per-kind fields (URL, target host/range, poll interval, profile). Secret fields render as masked inputs labeled `<set>`; changing a secret uses the dedicated write-only endpoint.
- Recent observations: last 50 observations this source produced, with link to the affected entity.
- Recent events: source-scoped events (poll failures, credential rotations, enable/disable).

**Per-kind context (terrain example):** The terrain-specific detail panel surfaces DNS stats (total/blocked queries, top clients, top blocked domains) and DHCP status (range, lease count) pulled from the latest successful poll. Other kinds surface their own kind-relevant summary (SNMP: interface counter snapshot; Nmap: last scan summary).

### Events

**URL:** `/events`

**Shows:** Activity log.

**Columns:** Timestamp, type, severity, source, summary.

**Filters:** Severity, type, source, date range.

---

## API Surface

### Agent API (authenticated by agent ID)

| Method | Path | Purpose |
|--------|------|---------|
| POST | /api/v1/agent/register | New agent registration |
| POST | /api/v1/agent/heartbeat | Liveness + status poll |
| POST | /api/v1/agent/metrics | Submit metric snapshots |
| POST | /api/v1/agent/discoveries | Submit neighbor sightings |
| POST | /api/v1/agent/inventory | Submit full system inventory |
| GET | /api/v1/releases/latest | Check for updates |
| GET | /api/v1/releases/download/{os}/{arch} | Download new binary |

### Admin API (authenticated by session cookie)

| Method | Path | Purpose |
|--------|------|---------|
| GET | /api/v1/agents | List agents |
| POST | /api/v1/agents/{id}/approve | Approve pending agent |
| POST | /api/v1/agents/{id}/deregister | Remove agent |
| GET | /api/v1/devices | List devices (hardware entities) |
| GET | /api/v1/devices/{id} | Device detail |
| POST | /api/v1/devices/{id}/edit | Update notes/tags |
| POST | /api/v1/devices/{id}/merge | Merge hardware entities |
| POST | /api/v1/devices/{id}/split | Split interface from group |
| GET | /api/v1/networks | List networks |
| POST | /api/v1/networks/{id}/status | Set network status |
| GET | /api/v1/alerts/rules | List alert rules |
| POST | /api/v1/alerts/rules | Create/update rule |
| GET | /api/v1/alerts/firings | List active firings |

### Response Format

All API responses are JSON with consistent envelope:
```json
{
    "data": { ... },
    "error": null
}
```

Error responses:
```json
{
    "data": null,
    "error": {
        "code": "not_found",
        "message": "device not found"
    }
}
```

---

## Store Query Contract

The presentation layer depends on store query methods that return view-ready data. Examples:

```go
// Device list — returns pre-aggregated hardware entities with counts
func (s *Store) ListHardware(ctx context.Context, filters HardwareFilters) ([]*HardwareView, error)

// Device detail — returns hardware + all children in one call
func (s *Store) GetHardwareDetail(ctx context.Context, id string) (*HardwareDetail, error)

// Agent metrics — returns time-bucketed data ready for graphing
func (s *Store) QueryMetrics(ctx context.Context, agentID string, window TimeWindow) (*MetricSeries, error)
```

These methods may execute complex SQL (CTEs, window functions, aggregations) but the handler doesn't know or care. The handler receives a struct and renders it.

---

## Frontend Technology

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| Templates | Go html/template | Server-rendered, no build step |
| CSS | Hand-rolled with custom properties | Dark mode via prefers-color-scheme, minimal size |
| JS | Vanilla + htmx where useful | No framework, no build pipeline, no node_modules |
| Graphs | d3-force (topology), Chart.js or similar (metrics) | CDN-loaded or vendored, ~10KB each |
| Interactivity | htmx for partial page updates | Inline editing, live search, tab switching without full reload |

### URL-Driven Navigation

All view state is encoded in the URL:
- Active tab: `/devices?tab=containers`
- Filters: `/devices?network=abc&kind=server&tag=production`
- Sort: `/devices?sort=last_seen&dir=desc`
- Pagination: `/devices?cursor=xyz`

Users can bookmark, share, and refresh without losing context. Tabs, filters, and sort update the URL via `history.replaceState` (no history pollution).

### Responsive

Mobile-friendly. Tables use horizontal scroll on narrow viewports. Navigation collapses to hamburger menu. Graphs resize via container queries.
