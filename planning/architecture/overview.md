# JMW.Agent Architecture (Lightweight)

> Single architecture doc covering all decisions. Lightweight workflow — replaces the per-record component/entity/API/dependency artifacts that the formal sdev architect would produce.

## Stack

| Layer | Choice | Rationale |
|---|---|---|
| Language | Go 1.23+ | Single binary, cross-compiles, low memory, stdlib coverage |
| Database | SQLite (modernc.org/sqlite, pure Go — no CGO) | Zero deps, single file, ARM-friendly |
| Server framework | stdlib `net/http` + `chi` router | Idiomatic, no magic; chi for clean routing |
| Templates | stdlib `html/template` | Server-rendered HTML |
| Frontend interactivity | Vanilla JS + sprinkles of htmx where useful | Zero build pipeline |
| CSS | Hand-rolled with CSS custom properties | Dark mode via `prefers-color-scheme` + token override |
| Config | TOML (BurntSushi/toml) + flags + env | Layered: defaults → file → env → flags |
| Logging | stdlib `log/slog` | Structured JSON in prod, human in dev |
| Auth | Server-side session cookies, bcrypt password hash | DEC-002, REQ-005 |
| Crypto | stdlib `crypto/tls`, `crypto/ed25519` | Self-signed cert at first boot; ed25519 for auto-update signing |
| Migrations | Plain `.sql` files in embedded FS, applied in order | No ORM, no migration framework — just versioned schema files |
| TLS | Self-signed bootstrap cert with optional user override | REQ-052 |
| Topology graph | `d3-force` (~10KB) loaded from CDN-or-vendored | Per UX critic suggestion — stop reinventing physics sims |

**Pure-stdlib first, exceptions documented:**
- `github.com/go-chi/chi/v5` — minimalist router; alternative is rolling our own `mux` matcher
- `modernc.org/sqlite` — pure-Go SQLite driver (no CGO; cross-compile easily)
- `github.com/BurntSushi/toml` — config parsing
- `golang.org/x/crypto/bcrypt` — password hashing
- `github.com/hashicorp/mdns` (or `github.com/grandcat/zeroconf`) — mDNS scanning, agent-side
- `github.com/google/gopacket` — only if we need raw ARP scanning beyond what stdlib provides

Every other capability comes from stdlib.

## Repo Layout

```
JMW.Agent/
├── go.mod
├── go.sum
├── README.md
├── Makefile                     # build, test, lint, cross-compile targets
├── cmd/
│   ├── server/                  # main package — server binary
│   │   └── main.go
│   └── agent/                   # main package — agent binary
│       └── main.go
├── internal/
│   ├── server/                  # server-only code
│   │   ├── http/                # handlers, middleware, router
│   │   │   ├── router.go
│   │   │   ├── auth.go
│   │   │   ├── api_agents.go
│   │   │   ├── api_metrics.go
│   │   │   ├── api_devices.go
│   │   │   ├── api_alerts.go
│   │   │   ├── ui_handlers.go   # html/template handlers
│   │   │   └── middleware.go
│   │   ├── store/               # SQLite repos — direct SQL, no ORM
│   │   │   ├── store.go         # connection, migrations
│   │   │   ├── agents.go
│   │   │   ├── metrics.go
│   │   │   ├── devices.go
│   │   │   ├── events.go
│   │   │   └── alerts.go
│   │   ├── alerting/            # rule evaluation engine
│   │   ├── notification/        # email, discord, pushover, gotify
│   │   ├── retention/           # tiered rollups + purge
│   │   ├── tls/                 # cert generation/loading
│   │   ├── session/             # cookie-based session store
│   │   ├── bootstrap/           # first-boot admin setup
│   │   └── server.go            # wires everything together
│   ├── agent/                   # agent-only code
│   │   ├── collect/             # system metrics collection
│   │   │   ├── linux.go
│   │   │   ├── darwin.go
│   │   │   ├── windows.go
│   │   │   └── common.go
│   │   ├── discover/            # ARP + mDNS scanning
│   │   ├── transport/           # outbound HTTPS to server
│   │   ├── update/              # auto-update with sig verify + rollback
│   │   ├── identity/            # agent ID file + keys
│   │   └── agent.go
│   ├── shared/                  # shared types between server + agent
│   │   ├── proto/               # API request/response shapes
│   │   ├── version/             # build-time version embedding
│   │   └── ids/                 # ID generation helpers
│   └── ui/                      # embedded UI assets
│       ├── templates/           # html/template files
│       │   ├── base.html
│       │   ├── dashboard.html
│       │   └── ...
│       ├── static/              # css, js, images
│       └── embed.go             # //go:embed directives
├── migrations/                  # SQL migration files (numbered)
│   ├── 0001_initial.sql
│   └── ...
├── deploy/                      # systemd/launchd unit files, install scripts
└── planning/                    # already exists
```

## Module Boundaries

- **`cmd/server`** and **`cmd/agent`** are thin: parse flags, load config, wire dependencies, start.
- **`internal/server`** never imports **`internal/agent`** and vice versa. Shared types live in **`internal/shared`**.
- **`internal/server/http`** depends on **`internal/server/store`** and other server modules but never the reverse.
- **`internal/server/store`** is the only package that touches SQL. Repos return domain types defined in `internal/shared/proto` or sub-packages.
- **`internal/agent/collect`** has per-OS files behind build tags (`//go:build linux`, etc.).

## Data Model (SQLite Schema Sketch)

```sql
-- registered agents (REQ-007/008)
CREATE TABLE agents (
  id TEXT PRIMARY KEY,                 -- UUID; the agent identity
  hostname TEXT NOT NULL,
  os TEXT NOT NULL,                    -- linux/darwin/windows
  arch TEXT NOT NULL,
  status TEXT NOT NULL,                -- pending|approved|deregistered
  approved_at TIMESTAMP,
  approved_by TEXT,
  registered_at TIMESTAMP NOT NULL,
  last_heartbeat_at TIMESTAMP,
  enabled_subsystems TEXT NOT NULL,    -- JSON array: ["metrics","discovery","latency","smart","docker"]
  current_version TEXT,                -- agent binary version
  notes TEXT,
  group_id INTEGER REFERENCES groups(id),
  PRIMARY KEY (id)
);
CREATE INDEX idx_agents_status ON agents(status);
CREATE INDEX idx_agents_last_heartbeat ON agents(last_heartbeat_at);

-- system metric snapshots (raw - REQ-021/022)
CREATE TABLE metric_snapshots (
  agent_id TEXT NOT NULL REFERENCES agents(id),
  ts TIMESTAMP NOT NULL,
  cpu_pct REAL,
  mem_used_bytes INTEGER,
  mem_total_bytes INTEGER,
  load_1 REAL, load_5 REAL, load_15 REAL,
  uptime_seconds INTEGER,
  PRIMARY KEY (agent_id, ts)
);
CREATE INDEX idx_metrics_ts ON metric_snapshots(ts);

-- rolled-up metrics (5min, hourly per REQ-022 retention)
CREATE TABLE metric_rollups_5min (...same shape, ts bucketed...);
CREATE TABLE metric_rollups_hourly (...);

-- per-disk metrics
CREATE TABLE disk_snapshots (
  agent_id TEXT NOT NULL,
  ts TIMESTAMP NOT NULL,
  device TEXT NOT NULL,
  mountpoint TEXT,
  used_bytes INTEGER,
  total_bytes INTEGER,
  read_iops REAL,
  write_iops REAL,
  smart_health TEXT,                   -- ok|warning|failing|unknown
  smart_attributes TEXT,               -- JSON
  PRIMARY KEY (agent_id, ts, device)
);

-- per-interface metrics + bandwidth
CREATE TABLE interface_snapshots (
  agent_id TEXT NOT NULL,
  ts TIMESTAMP NOT NULL,
  iface TEXT NOT NULL,
  ip TEXT, mac TEXT,
  rx_bytes INTEGER, tx_bytes INTEGER,
  rx_packets INTEGER, tx_packets INTEGER,
  PRIMARY KEY (agent_id, ts, iface)
);

-- discovered devices (DEC-001)
CREATE TABLE devices (
  id INTEGER PRIMARY KEY,              -- internal stable ID
  mac TEXT NOT NULL,                   -- canonical dedup key
  canonical_hostname TEXT,
  canonical_ip TEXT,
  vendor TEXT,                         -- from MAC OUI lookup
  device_class TEXT,                   -- server|desktop|phone|printer|iot|unknown
  device_class_source TEXT,            -- auto|manual
  status TEXT NOT NULL,                -- active|ignored|archived
  first_seen_at TIMESTAMP NOT NULL,
  last_seen_at TIMESTAMP NOT NULL,
  group_id INTEGER REFERENCES groups(id),
  agent_link TEXT REFERENCES agents(id), -- if this device IS an agent
  UNIQUE(mac)
);

-- per-observer device sightings
CREATE TABLE device_sightings (
  device_id INTEGER NOT NULL REFERENCES devices(id),
  observer_agent_id TEXT NOT NULL REFERENCES agents(id),
  subnet TEXT NOT NULL,
  ip TEXT, hostname TEXT,
  mdns_services TEXT,                  -- JSON
  last_seen_at TIMESTAMP NOT NULL,
  PRIMARY KEY (device_id, observer_agent_id)
);

-- tags (many-to-many for both agents and devices via polymorphic)
CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);
CREATE TABLE tag_assignments (
  tag_id INTEGER NOT NULL,
  target_kind TEXT NOT NULL,           -- 'agent' | 'device'
  target_id TEXT NOT NULL,             -- agent.id (text) or device.id (int as text)
  PRIMARY KEY (tag_id, target_kind, target_id)
);

CREATE TABLE groups (id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL);

-- events / activity log (REQ-018)
CREATE TABLE events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TIMESTAMP NOT NULL,
  type TEXT NOT NULL,                  -- agent.registered|agent.offline|alert.fired|...
  severity TEXT NOT NULL,              -- info|warning|critical
  source_kind TEXT,                    -- agent|device|system|user
  source_id TEXT,
  summary TEXT NOT NULL,
  detail_json TEXT
);
CREATE INDEX idx_events_ts ON events(ts);
CREATE INDEX idx_events_type ON events(type);

-- alert rules (REQ-023)
CREATE TABLE alert_rules (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  rule_kind TEXT NOT NULL,             -- offline|disk_pct|cpu_pct|latency|packet_loss|...
  scope_json TEXT,                     -- which agents/devices this applies to
  threshold REAL,
  sustain_seconds INTEGER,
  channel_ids TEXT,                    -- JSON array
  created_at TIMESTAMP NOT NULL
);

-- firing alerts
CREATE TABLE alert_firings (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  rule_id INTEGER NOT NULL REFERENCES alert_rules(id),
  target_kind TEXT NOT NULL,
  target_id TEXT NOT NULL,
  fired_at TIMESTAMP NOT NULL,
  resolved_at TIMESTAMP,
  message TEXT
);

-- notification channels (REQ-024)
CREATE TABLE notification_channels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,                  -- email|discord|pushover|gotify
  config_json TEXT NOT NULL,           -- channel-specific (encrypted at rest later)
  enabled INTEGER NOT NULL DEFAULT 1
);

-- users (only one in MVP, but extensible)
CREATE TABLE users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  username TEXT UNIQUE NOT NULL,
  password_hash TEXT NOT NULL,
  created_at TIMESTAMP NOT NULL
);

-- sessions
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,                 -- random 256-bit token
  user_id INTEGER NOT NULL,
  created_at TIMESTAMP NOT NULL,
  expires_at TIMESTAMP NOT NULL,
  last_used_at TIMESTAMP NOT NULL
);

-- system config (PSK, retention overrides, etc.)
CREATE TABLE config (key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

Schema is concrete enough to start; will evolve via numbered migrations.

## API Surface (Versioned `/api/v1/`)

**Agent → Server (machine APIs):**

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/agents/register` | PSK or unauth | Submit registration; server returns approved/pending |
| POST | `/api/v1/agents/{id}/heartbeat` | mTLS or token | Liveness + version check + enabled-subsystem set |
| POST | `/api/v1/agents/{id}/metrics` | mTLS or token | Submit a batch of metric snapshots |
| POST | `/api/v1/agents/{id}/discoveries` | mTLS or token | Submit ARP/mDNS observations |
| GET | `/api/v1/agents/{id}/update-info` | mTLS or token | Get latest binary version + sig URL |
| GET | `/api/v1/agents/{id}/binary/{version}` | mTLS or token | Download new binary (signed) |

**Browser → Server (UI APIs, session-cookie auth):**

| Method | Path | Purpose |
|---|---|---|
| GET | `/` | Dashboard (HTML) |
| GET | `/agents` | List view |
| GET | `/agents/{id}` | Detail view |
| GET | `/devices` | Discovered devices |
| GET | `/topology` | Network map |
| GET | `/alerts` | Alerts view |
| GET | `/events` | Event log |
| GET | `/settings/...` | Settings sub-pages |
| POST | `/login`, `/logout` | Auth |
| GET | `/setup` | First-boot wizard (only when no users exist) |
| POST | `/api/v1/admin/agents/{id}/approve` | Approve pending |
| POST | `/api/v1/admin/agents/{id}/deregister` | Remove agent |
| POST | `/api/v1/admin/devices/{id}/ignore` | Ignore device |
| POST | `/api/v1/admin/alerts/rules` | Create rule |
| `...` | `...` | etc. |

**Stable API contract rule:** machine APIs `/api/v1/agents/*` are versioned. UI handlers that return HTML are not versioned (they can change freely).

## Security

- **Dashboard TLS:** auto-generated self-signed cert at first boot, persisted to `data/server-cert.pem` + `data/server-key.pem`. User can replace with their own. Plain HTTP only via `--insecure-http` flag.
- **Agent transport:** HTTPS to server. Agent pins server's cert fingerprint at first registration. Cert rotation requires manual `--rotate-pin` flag on agent or short PSK re-handshake — documented in REQ-052 mitigation.
- **PSK:** optional pre-shared key set at server first boot. Agents presenting it during registration are auto-approved. PSK is hashed (bcrypt) at rest.
- **Auth:** bcrypt password hashing (cost 12), session cookies (256-bit random ID, 30-day default expiry, HttpOnly, Secure, SameSite=Lax).
- **Auto-update signing:** ed25519. Public key embedded in agent binary at install/build time (REQ-045 AC #3 — never retrieved from server).
- **CSRF:** double-submit cookie pattern on state-changing UI POSTs.
- **Rate limiting:** in-process token bucket on `/login` and `/api/v1/agents/register`.

## Concurrency Model

- Server is a single process. Goroutines for: HTTP handlers (one per request), heartbeat-staleness checker (1 ticker), retention rollup job (1 ticker), alert evaluator (1 ticker, wakes on metric ingest signal), notification dispatcher (worker pool).
- SQLite WAL mode, single writer goroutine via channel-fed serialization for write-heavy paths (metric ingest). Reads are concurrent.
- Agent has a small set of goroutines: collector ticker, discovery ticker, transport sender, update checker.

## Configuration

```toml
# server.toml example
[server]
listen = ":8443"
data_dir = "./data"
session_lifetime = "720h"

[tls]
mode = "self-signed"            # self-signed | provided | insecure-http
cert_file = ""                  # if mode = provided
key_file = ""

[retention]
raw_metrics = "7d"
five_min_rollups = "30d"
hourly_rollups = "365d"
events = "180d"

[psk]
hashed = ""                     # set via setup wizard or CLI
```

Agent config is similarly minimal: server URL, agent ID file path, heartbeat interval, enabled subsystems.

## Build & Cross-Compile

- `Makefile` targets: `build`, `test`, `lint`, `build-all` (all platforms), `release`.
- `build-all` produces: `server-linux-amd64`, `server-linux-arm64`, `server-darwin-amd64`, `server-darwin-arm64`, and matching `agent-*` binaries. Windows agent is should-have so not in default build-all.
- Version embedding via `-ldflags="-X internal/shared/version.Version=$(git describe)"`.
- No CGO (`CGO_ENABLED=0`) — pure-Go SQLite driver makes this possible.

## What's Deferred / Out of Scope

- ORM, migration framework, dependency injection container — direct code throughout
- gRPC — REST is fine for this scale
- WebSockets / SSE — polling is in spec
- Multi-user / RBAC — single user
- LDAP/OAuth — local auth only
- Cloud-native niceties (k8s, helm, etc.) — bare-metal/systemd/launchd targets
