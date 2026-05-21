# JMW.Agent Implementation Plan (Lightweight)

## Phasing Strategy

Vertical slices: each phase delivers a runnable, useful product end-to-end. No phase requires another phase to be useful. Later phases layer features onto an already-working foundation.

| Phase | Deliverable | What works after this phase |
|---|---|---|
| **P1: Foundation** | Bootable server + agent, registration, approval, basic system info display | Boss installs agent on a Linux box, points it at the server, approves it from a (basic) UI, sees CPU/memory/disk/uptime in a (basic) detail page. |
| **P2: UI polish + dashboard** | Full dashboard with summary cards, client list, dark mode, navigation, event log | The dashboard looks like the usability design. Every UI surface from the usability design (S-1..S-13) at least exists, even if some are stubs. |
| **P3: Historical metrics + alerts** | Time-series storage, retention rollups, alert rules, sparklines, notification channels | Boss gets a Discord ping when his Pi runs out of disk. Sees a sparkline of CPU history. |
| **P4: Network discovery** | Agent ARP + mDNS scanning, discovered devices view, dedup, classification | Boss sees printers, Google Homes, IoT devices on his IoT VLAN (via an agent on that subnet) without installing anything on them. |
| **P5: Operations** | Agent auto-update, SMART, Docker, listening services, OS update status, backups, topology map | Pushing a new agent version to all hosts from the server. SMART monitoring on physical disks. Visual network topology. |

P1 takes the bulk of the foundational work (HTTP, DB, auth, cert handling, agent transport, basic collection). P2-P5 each layer on without disturbing what's running.

---

## Phase 1 — Foundation

**Goal:** end-to-end working system. Server runs, agent runs, agent registers, admin approves, basic data flows through.

### Work Items

| WI | Title | Notes |
|---|---|---|
| P1-01 | Repo scaffold + Go module + Makefile | `go mod init`, build/test/lint targets, basic CI placeholder. |
| P1-02 | Shared types + version embedding | `internal/shared/proto`, `internal/shared/version`. Build-time version inject. |
| P1-03 | SQLite store + migrations | `modernc.org/sqlite`, embedded `migrations/*.sql`, applied on boot. Schema for agents/users/sessions/events/config tables. |
| P1-04 | TLS bootstrap | Self-signed cert generation at first boot, persisted to data dir. `--insecure-http` flag for opt-out. Embedded into HTTPS server. |
| P1-05 | First-boot setup wizard | Detect empty users table → redirect to `/setup`. Wizard creates admin user + optionally sets PSK. |
| P1-06 | Login / sessions / CSRF | bcrypt password hashing, cookie-based sessions in SQLite, login form, logout, CSRF middleware. |
| P1-07 | Server config loader | TOML + env + flags, layered. |
| P1-08 | Server skeleton + chi router | Routes registered, middleware (logger, auth, csrf), `/login`, `/setup`, basic `/` placeholder. |
| P1-09 | Agent registration API | `POST /api/v1/agents/register` — accepts PSK, returns approved/pending status + server cert fingerprint for pinning. |
| P1-10 | Agent identity + key persistence | Agent persists its UUID, generated keypair, server URL, pinned cert fingerprint to a config file. |
| P1-11 | Agent heartbeat API + client | `POST /api/v1/agents/{id}/heartbeat` server-side; agent client ticker. Updates `last_heartbeat_at`. |
| P1-12 | Agent system info collection (linux+darwin) | CPU%, memory, disks, uptime, OS info, network interfaces. Build-tagged per-OS files. Stdlib first. |
| P1-13 | Metric ingestion API | `POST /api/v1/agents/{id}/metrics` — raw snapshot persistence. |
| P1-14 | Pending agents UI (basic) | Server-rendered HTML page listing pending agents, approve/reject buttons. |
| P1-15 | Agent list UI (basic) | Lists registered+approved agents with status dot. |
| P1-16 | Agent detail UI (basic) | Shows last heartbeat, system info snapshot. No tabs yet, no history. |
| P1-17 | systemd unit + launchd plist + install scripts | `deploy/` directory. Installation as a service. |
| P1-18 | Smoke test | Run server + 2 agents (one local, one in docker), verify register→approve→heartbeat→metrics flow. |

### Phase 1 Done Criteria

- `make build-all` produces server + agent binaries for linux-amd64, linux-arm64, darwin-arm64
- Run `./server` on first boot → setup wizard creates admin
- Run `./agent --server=https://localhost:8443` → registers, appears in pending queue
- Approve in UI → agent starts heartbeating, appears in list, basic metrics visible
- All Phase 1 features tested with go test + 1 smoke test script

---

## Phase 2 — UI Polish

**Goal:** The dashboard from the usability design comes to life.

### Work Items

| WI | Title | Notes |
|---|---|---|
| P2-01 | Design system CSS | Tokens, light + dark modes via prefers-color-scheme, typography scale. |
| P2-02 | Base templates + layout | `base.html`, sidebar nav (CC-1, 9 items), header, content area. Polling JS module. |
| P2-03 | Dashboard summary cards | At-a-glance: devices online, alerts firing, total disk free (with caveats per S-3 critique), uptime, recent events. |
| P2-04 | Activity feed widget | Rolling event list with severity colors. |
| P2-05 | Polished agent list | Dense table, sortable, filterable, search, status dots, last-seen relative time. |
| P2-06 | Polished agent detail (overview tab) | Header (status, hostname, IP, OS), key stats, last events. Other tabs stubbed. |
| P2-07 | Tags + groups CRUD | Manage tags, assign to agents/devices. |
| P2-08 | Event log full view | Filtered, paginated, exportable to CSV. |
| P2-09 | Settings shell | Sub-nav for TLS / retention / PSK / channels (most read-only or stubbed in P2). |
| P2-10 | Confirmation patterns | Toast-undo, modal, type-to-confirm — implement once, use everywhere. |
| P2-11 | Empty states | Per usability design CC-5. |

### Phase 2 Done Criteria

- All 13 surfaces from S-1..S-13 are at least navigable (some still stubs).
- Dark mode works.
- Confirmation patterns implemented.
- A new visitor walks through setup → dashboard → agents → detail without confusion.

---

## Phase 3 — Metrics + Alerts

**Goal:** Historical data + alerting.

### Work Items

| WI | Title | Notes |
|---|---|---|
| P3-01 | Tiered retention + rollup job | 5-min and hourly rollups. Cron-style ticker. |
| P3-02 | Sparkline rendering | SVG inline, no JS chart lib needed for sparklines. |
| P3-03 | Time-series query helpers | `Query(agent, metric, range, granularity)` returns smart-tier data. |
| P3-04 | Alert rule model + CRUD UI | Create/edit/delete rules in side drawer. |
| P3-05 | Alert evaluation engine | Tickers per rule kind: offline, disk%, CPU%, latency, packet loss. Sustain duration support. |
| P3-06 | Alert firing storage + UI | Firing alerts list, resolved alerts, severity. |
| P3-07 | Notification channels CRUD | Email (SMTP), Discord webhook, Pushover, Gotify. Test button. |
| P3-08 | Notification dispatch worker | Worker pool, retry, dedup window. |
| P3-09 | Per-disk metrics + I/O | Read/write IOPS, latency. |
| P3-10 | Per-interface bandwidth | Delta tracking, sparkline. |
| P3-11 | Uptime / reboot history | Heartbeat gap detection → reboot event. |

### Phase 3 Done Criteria

- A disk-full rule fires within budget when a Pi crosses threshold.
- Discord webhook receives the alert.
- Sparklines render on dashboard + detail views.
- Reboot history visible per agent.

---

## Phase 4 — Network Discovery

**Goal:** see things you don't have agents on.

### Work Items

| WI | Title | Notes |
|---|---|---|
| P4-01 | Agent ARP scanner | Per-subnet, throttled, stdlib-friendly approach (or `gopacket` if needed). |
| P4-02 | Agent mDNS listener + scanner | Listen passively + active queries. Surface common services. |
| P4-03 | Discovery API + ingest | `POST /api/v1/agents/{id}/discoveries`. |
| P4-04 | Server-side dedup + canonicalization | MAC as key, precedence rules per REQ-035. |
| P4-05 | OUI/vendor lookup | Embedded OUI database (small). |
| P4-06 | Auto-classification | Rules engine per REQ-042 (printer, IoT, Google cast, etc.). Manual override. |
| P4-07 | Devices list view (3 tabs) | Unified / per-subnet / per-observer. |
| P4-08 | Device detail (agentless) | Variant of agent detail showing only what we discovered. |
| P4-09 | Latency monitoring | Agents ping configured devices on subnet, report RTT/loss. |
| P4-10 | DNS resolution tracking | Per-agent DNS query logging (sampling). |
| P4-11 | DHCP awareness | Detect static-vs-dynamic via lease watching where possible. |

### Phase 4 Done Criteria

- Agent on IoT VLAN reports printers/Google Homes/Chromecasts.
- Server unifies sightings across multiple agents.
- Auto-classification gets the obvious cases right.
- Three discovered-device views work.

---

## Phase 5 — Operations

**Goal:** quality-of-life.

### Work Items

| WI | Title | Notes |
|---|---|---|
| P5-01 | Agent auto-update | Server holds latest binary + signature. Agent pulls, verifies (ed25519, install-time-embedded key), restarts. Auto-rollback on 3 failed starts in 60s. |
| P5-02 | SMART data | Linux: `/sys/block/*/device/`, parse smartctl JSON if available. macOS: best-effort. |
| P5-03 | Docker container listing | Read `/var/run/docker.sock` (where available). |
| P5-04 | Listening services / port snapshot | Per-OS implementation. Diff detection for "new port appeared." |
| P5-05 | OS update status | apt/dnf/brew check, parsed. |
| P5-06 | Topology graph | d3-force vendored, click-through to detail. |
| P5-07 | Backup CLI + UI | `jmw-server backup` + `jmw-server restore <file>`. UI download + upload. |
| P5-08 | Scheduled snapshots | Daily SQLite snapshot to data dir. Configurable. |

### Phase 5 Done Criteria

- Push agent v2 from server, see all agents update in turn.
- See SMART status per disk on Boss's NAS.
- Docker container list per agent.
- Topology map renders Boss's home network legibly.
- Backup file restores cleanly into a fresh DB.

---

## What We Do Per WI (Lightweight Quality Gate)

Per work item:
1. Implement
2. Run `go test ./...` and `go vet ./...`
3. Run `gofmt`/`goimports` (lint)
4. Manual sanity check (build, run, exercise the feature)

After each phase:
- Smoke test script run end-to-end
- Memory / progress note updated
- Boss checkpoints

**Skipped per "lightweight" mode:** per-WI security audit, supply chain audit, API contract audit, accessibility audit, error-handling audit, data integrity audit, full QA test specifications. The architecture has security baked in (TLS, bcrypt, CSRF, sig-verified updates); we don't need a 6-step audit cycle to verify it.

**Will revisit:** if/when this graduates from "personal project" to "share with others," a full security audit pass is justified.
