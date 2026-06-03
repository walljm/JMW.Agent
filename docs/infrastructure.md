# Infrastructure

Cross-cutting concerns that support the application but aren't specific to any one feature: authentication, TLS, configuration, data retention, and deployment.

## Authentication & Sessions

### Model

Server-side sessions with secure cookies. No JWTs.

| Component | Implementation |
|-----------|---------------|
| Password storage | bcrypt hash (cost 12) |
| Session token | 32-byte random, stored server-side |
| Cookie | httpOnly, Secure, SameSite=Lax, 24h expiry |
| CSRF | Double-submit cookie pattern on all POST/PUT/DELETE |

### First-Boot Bootstrap

On first server start (no users in database):
1. Server generates a random bootstrap token and prints it to stdout/logs.
2. Admin visits the UI, sees a "Create admin account" form that requires the bootstrap token.
3. Once the first admin is created, bootstrap is disabled permanently.

No default passwords. No hardcoded credentials.

### Password Recovery

If the admin forgets their password:
```sh
jmw-server reset-password --user admin
```
Generates a new random password, prints it to stdout, updates the hash in the database. Requires local CLI access to the server (intentional — no email-based reset for a LAN-only tool).

### Session Lifecycle

- Created on successful login.
- Extended on each request (sliding expiry).
- Destroyed on explicit logout.
- Purged by background job when expired.
- No concurrent session limit (single admin is typical, but multiple are allowed).

---

## TLS

### Self-Signed Bootstrap

On first server start:
1. Generate ECDSA P-256 private key.
2. Create self-signed X.509 certificate (CN = hostname, SANs = all local IPs).
3. Write to `data/tls/server.key` and `data/tls/server.crt`.
4. Serve HTTPS immediately.

### User-Provided Certificate

If the admin provides their own cert/key (e.g., from a local CA or Let's Encrypt):
```toml
[tls]
cert_file = "/path/to/cert.pem"
key_file = "/path/to/key.pem"
```
Server uses the provided cert instead of self-signed. Reloads on SIGHUP without restart.

### Agent Cert Pinning

Agents pin the server's certificate SHA-256 fingerprint on first successful connection (trust-on-first-use). This prevents MITM attacks on the LAN without requiring a full PKI infrastructure.

If the server cert changes legitimately (rotation, user-provided cert replaces self-signed), agents must either:
- Have their pin file deleted (re-pins on next connection)
- Be told the new fingerprint via config

---

## Configuration

### What Belongs in `server.toml` vs the Database

The TOML file holds **only the configuration the process needs to start**. Everything else lives in the SQLite database and is managed through the admin UI.

| Lives in `server.toml` | Lives in the database |
|------------------------|----------------------|
| Listen address, data dir | Polled-source definitions (terrain DHCP/DNS, SNMP, Nmap, future kinds) |
| TLS cert/key paths (or empty for self-signed) | Source credentials (encrypted at rest, referenced by ID) |
| Database file path | Notification channels |
| Log level | Alert rules and firings |
| Bootstrap seed (one-time admin token, see `agent-lifecycle.md`) | Users and sessions |
| Retention defaults (overridable per-tier via UI) | Retention overrides |
| | Tags, custom views, dashboard layouts |

**Why no file-based source config.** Polled sources are runtime-mutable, per-instance, and credential-bearing. Putting them in a TOML file means: operators edit the file and restart, credentials sit on disk in cleartext (or get pulled from a side-channel we now have to design), there's no audit trail of "who added which source when," and the UI either lies about its state or is read-only. None of that is worth it. The source-of-truth is the `sources` table; the UI is a CRUD view over it.

The previous `[terrain]` block in `server.toml` is **removed**. On first start, if the database has no sources and the legacy `[terrain]` block is present, the server logs a one-time migration notice, imports the values into a `sources` row, and ignores the block thereafter. After one release cycle the migration shim is deleted.

### Server Configuration (`server.toml`)

```toml
[server]
listen = ":8443"
data_dir = "./data"
log_level = "info"

[tls]
cert_file = ""  # empty = self-signed
key_file = ""

[retention]
# Defaults — overridable per-tier from the UI without restart
raw_metrics = "48h"
rollup_5min = "7d"
rollup_hourly = "90d"
rollup_daily = "365d"
removed_containers = "7d"
stale_observations = ""  # empty = keep forever
```

No `[terrain]`, no `[notifications]`, no `[sources]`. The file is intentionally short: anything an operator would want to change at 2am without redeploying must live in the UI.

### Secret Storage

Several `config_json` columns may carry secrets — currently `sources.config_json` (terrain passwords, SNMP communities, API tokens) and `notification_channels.config_json` (SMTP credentials, webhook auth headers). The same mechanism applies to every secret-bearing column:

1. The server generates a per-installation **data encryption key (DEK)** at first start, stored at `$data_dir/server.key` with file mode `0600`. This file is the root of trust for all `config_json` secrets; back it up alongside the database.
2. Secret-bearing fields inside `config_json` are encrypted with AES-256-GCM using the DEK before insert and decrypted on read by the consumer (an adapter for sources, the notifier for channels).
3. The UI never returns secret fields in API responses — it returns the sentinel `"<set>"` and accepts updates via a separate write-only endpoint (`PATCH /api/v1/sources/{id}/secrets`, `PATCH /api/v1/channels/{id}/secrets`).
4. Rotating the DEK is a single CLI operation that re-encrypts every secret-bearing row across `sources` and `notification_channels` in one transaction.

Any new entity that grows a `config_json` column carrying secrets MUST use the DEK — no per-table key derivation, no plaintext fallback. The Foundation Critic verifies that no code path returns a decrypted secret to an API response.

This keeps secrets out of `server.toml`, out of process arguments, out of environment variables, and out of UI read responses, while keeping operations simple (one file to back up beyond the DB).

### Agent Configuration (`agent.toml`)

```toml
server_url = "https://192.168.1.100:8443"
psk = ""

[subsystems]
metrics = true
discovery = true
disk = true
docker = true
smart = true

[intervals]
metrics = "30s"
discovery = "5m"
inventory = "24h"
heartbeat = "30s"
update_check = "24h"

[discovery]
excluded_interfaces = []
```

### Layered Resolution

Both server and agent configs resolve in order (later overrides earlier):
1. Compiled defaults (always sufficient for basic operation)
2. TOML config file (if present — missing file is not an error)
3. Environment variables (`JMW_SERVER_LISTEN`, `JMW_AGENT_SERVER_URL`, etc.)
4. CLI flags (`--listen`, `--server-url`, etc.)

---

## Designed Scale

The architecture is explicitly sized for a single home-lab / small-office network. Concrete boundaries:

| Dimension | Target | Hard ceiling | Beyond this you need |
|-----------|--------|--------------|---------------------|
| Agents | ≤ 30 | ≤ 50 | Sharding by agent group or external time-series store |
| Tracked devices (Hardware entities) | ≤ 500 | ≤ 2,000 | Read-replica + denormalized list cache |
| Observations / day | ≤ 200,000 | ≤ 1,000,000 | Tighter observation retention or partitioning |
| Snapshot writes / second (steady state) | ≤ 30 | ≤ 100 | Per-agent write batching window > 5s, or external TSDB |
| Sources (polled) | ≤ 20 | ≤ 50 | Adapter-internal connection pooling |
| Concurrent admin UI users | ≤ 5 | ≤ 20 | Per-handler query timeout enforcement |

**Single server, single SQLite file, single writer.** No clustering, no replication, no horizontal scale. This is a deliberate choice for the deployment shape — a NAS, a Raspberry Pi, or a small VM running one binary. The numbers above are operating assumptions, not benchmarked maxima; a smoke test in `internal/smoke/` should keep them honest.

**Write-batching policy.** Snapshot ingest batches all snapshot-table writes for one agent `/tick` request into a single transaction (one BEGIN, one COMMIT, all 7 snapshot tables). The pipeline writer holds the WAL writer lock for that transaction only; the alert evaluator and other ingests queue behind it. If the writer falls behind (`writer_queue_depth > 100` requests waiting), the server returns `503 Service Unavailable` with `Retry-After` set to twice the agent's tick interval — agents back off, do not retry-storm, and the next tick coalesces missed samples. This back-pressure is the only graceful-degradation mechanism; without it, a sustained overload silently grows the WAL file until disk fills.

---

## Data Retention

### Metrics Retention

Handled by the rollup system (see [Metrics & Alerting](metrics-and-alerting.md)):
- Raw snapshots: 48 hours → rolled into 5-min averages
- 5-min rollups: 7 days → rolled into hourly
- Hourly rollups: 90 days → rolled into daily
- Daily rollups: 1 year → deleted

### Entity Retention

| Entity type | Retention rule |
|-------------|---------------|
| Hardware | Never auto-deleted (physical things don't disappear) |
| System (persistent) | Never auto-deleted (may go offline, still exists) |
| System (ephemeral) | Deleted after `removed_containers` period post-removal |
| Interface | Never auto-deleted (MAC is permanent record) |
| Observation | Kept indefinitely (bucketed, bounded by key count) or pruned after configurable age |
| Hostname aliases | Kept indefinitely (bounded by observation count, not time) |
| Alert firings | Kept indefinitely (audit trail) |
| Events | Configurable max age (default: 90 days) |
| Sessions | Deleted on expiry |
| Pending agents | Deleted after 72h without approval |

### Cleanup Job

Background goroutine runs hourly:
1. Metric rollups (promote and delete aged-out tiers).
2. Prune removed ephemeral systems past retention window.
3. Delete expired sessions.
4. Delete expired pending agent registrations.
5. Optionally: prune old events, old observations (if configured).

---

## Database

### Engine

SQLite via `modernc.org/sqlite` (pure Go, no CGO). Single file at `data/jmw.db`.

### WAL Mode

Write-Ahead Logging enabled for concurrent read access during writes. Single writer (serialized by Go mutex), multiple readers (handlers serving UI/API).

### Migrations

Plain SQL files in an embedded filesystem (`migrations/`), applied in lexicographic order on startup:
```
migrations/
├── 0001_initial.sql
├── 0002_device_sightings.sql
├── 0003_alerts.sql
├── ...
└── 00XX_entity_model_v2.sql  (future: new schema)
```

Migration state tracked in a `schema_version` table. Each migration runs in a transaction — partial failures roll back cleanly.

### Connection Management

Single `*sql.DB` instance shared across the application. SQLite doesn't benefit from connection pooling (single-writer semantics). All writes go through the Store layer — no package outside `internal/server/store/` touches SQL directly. The Foundation Critic enforces this with a grep over the server packages.

### Store Sub-Module Organization

`internal/server/store/` is split into focused files. The package presents a single `Store` interface to callers, but the internal organization keeps each file scoped to one concern. This is internal file organization only — the public API is unchanged.

| File | Owns | Read/Write |
|------|------|-----------|
| `store/pipeline.go` | Writes from the data pipeline: Hardware, System, Interface, Service, Disk, Observation, Hostname Aliases, structured profile tables | RW |
| `store/posture.go` | Host-posture extension tables (update_status, security_posture, firewall_*, antivirus_products, encrypted_volumes, failed_services, listening_ports, local_users, logged_in_sessions, routes, packages, hassio_*) | RW (pipeline writes; views.go reads) |
| `store/containers.go` | Container metadata, container_mounts, container_ports, docker_images, docker_volumes, docker_networks_engine | RW |
| `store/metrics.go` | All time-series snapshot tables (metric_snapshots, disk_snapshots, interface_snapshots, filesystem_snapshots, temperature_snapshots, battery_snapshots, processes_snapshot) plus rollup writers | RW |
| `store/sources.go` | Source rows, poll-state tracking (`last_success_at`, `last_error_at`, `consecutive_error_count`), enable/disable, credential references | RW |
| `store/agents.go` | Agent registration lifecycle, agent_subsystems junction, agent_inventory_receipts, agent_unknown_sections, subsystems registry | RW |
| `store/alerts.go` | Alert rules, alert_firings (the second-writer surface, see data-pipeline.md → Alert Evaluator as a Second Writer), notification channels | RW |
| `store/events.go` | Append-only Events table | W from pipeline + alerts; R from views |
| `store/auth.go` | Users, Sessions, password hashing helpers, bootstrap token state | RW |
| `store/tags.go` | Tags table, tag aggregation queries | RW |
| `store/views.go` | Read-side: complex JOIN-heavy queries that back UI pages (hardware detail, agent detail, dashboard rollups). No writes. | R only |
| `store/migrations.go` | Schema migration runner, `schema_version` table | RW (schema only) |
| `store/store.go` | `Store` struct, dependency wiring, transaction helpers | — |

**Rules:**

1. **No file writes outside its lane.** `views.go` is read-only; `events.go` is write-only from upstream callers; `posture.go` is the only writer of posture tables. The Foundation Critic flags cross-file write violations.
2. **One transaction per public method.** Cross-table updates that must atomically succeed live in a single method on the public `Store` interface, which opens one transaction and dispatches to the file-internal helpers. Helpers take `*sql.Tx`, not `*sql.DB`.
3. **`views.go` queries do not call write methods.** A read path must not lazily write (no "cache miss → upsert"). The Derive stage of the pipeline owns all materialization.
4. **Per-file test files.** `store/pipeline_test.go`, `store/posture_test.go`, etc., each exercising only their file's surface.

The split is mechanical refactoring with no behavior change. It exists because the previous monolithic `store.go` had become the single largest file in the server and was attracting unrelated concerns. Each file should stay under ~600 lines; if one grows past that, split it again.

### Backup

SQLite's built-in backup API (`VACUUM INTO '/path/to/backup.db'`) provides consistent point-in-time snapshots. Admin can trigger via CLI or schedule via cron.

---

## Deployment

### Single Binary

Both server and agent are single statically-linked binaries (`CGO_ENABLED=0`). No runtime dependencies. Drop the binary, run it.

### Service Management

| Platform | Server | Agent |
|----------|--------|-------|
| Linux | systemd unit file | systemd unit file |
| macOS | launchd plist | launchd plist |
| Docker | N/A (runs natively) | Dockerfile (distroless base) |
| Windows | N/A | PowerShell install script → Windows Service |

### Docker Agent

For NAS deployments where native binary installation isn't practical:
```sh
docker run -d --name jmw-agent \
  --restart=always \
  --network=host --pid=host --privileged \
  -v /:/host:ro \
  -v /volume1/jmw-agent/data:/data \
  -v /volume1/jmw-agent/etc:/etc/jmw-agent:ro \
  walljm/jmw-agent:latest
```

Critical flags:
- `--network=host` — sees real network interfaces, not Docker bridge
- `--pid=host` — sees real processes
- `--privileged` — SMART access to raw block devices
- `-v /:/host:ro` — host filesystem access for /proc, /sys metrics
- `JMW_HOST_ROOT=/host` — tells agent to prefix all /proc /sys reads

### Watchtower

NAS deployments use Watchtower for automatic container updates. Push a new image tag → Watchtower pulls and restarts within its poll interval. No manual SSH required for routine updates.

---

## Logging

### Format

`log/slog` with:
- JSON output in production (structured, machine-parseable)
- Human-readable text in development (`-dev` flag)

### Levels

| Level | Usage |
|-------|-------|
| DEBUG | Protocol-level details (individual ARP replies, SQL queries) |
| INFO | Normal operations (agent registered, discovery completed, poll succeeded) |
| WARN | Recoverable issues (terrain poll failed, single device probe timeout, retry) |
| ERROR | Failures requiring attention (DB write failed, cert expired, agent can't reach server) |

### No Log Rotation

Logs go to stdout/stderr. Service managers (systemd journald, Docker log driver) handle rotation. The application doesn't write log files or manage rotation.

---

## Build & Release

### Makefile Targets

```sh
make build          # bin/jmw-server + bin/jmw-agent (host OS)
make build-all      # Cross-compile: linux/darwin × amd64/arm64
make test           # go test ./...
make vet            # go vet ./...
make docker-agent   # Build + push Docker image
```

### Versioning

Semver. Version embedded at build time via `-ldflags`:
```sh
go build -ldflags "-X internal/shared/version.Version=1.5.0" ./cmd/agent
```

### Release Artifacts

For each release:
- `jmw-agent-linux-amd64`
- `jmw-agent-linux-arm64`
- `jmw-agent-darwin-amd64`
- `jmw-agent-darwin-arm64`
- `jmw-server-linux-amd64`
- `jmw-server-linux-arm64`
- `jmw-server-darwin-amd64`
- `jmw-server-darwin-arm64`
- Docker image: `walljm/jmw-agent:latest` + `walljm/jmw-agent:v1.X.Y`
- Home Assistant add-on image: `walljm/jmw-agent-ha:latest` + `walljm/jmw-agent-ha:1.X.Y`

---

## Agent Auto-Update

Agents self-update when the server publishes a newer version. This is the primary upgrade mechanism for native (non-Docker) deployments.

### Server-Side: Release Directory

The server scans a `releases/` directory for versioned agent binaries:

```
data/releases/
  v1.3.0/
    jmw-agent-linux-amd64
    jmw-agent-linux-arm64
    jmw-agent-darwin-amd64
    jmw-agent-darwin-arm64
    jmw-agent-windows-amd64.exe
  v1.4.0/
    ...
```

**Naming convention:** `jmw-agent-{os}-{arch}[.exe]`. Directory must be a valid semver tag (`vX.Y.Z`).

**Resolution:** The server selects the highest semver directory that contains a binary matching the requesting agent's OS/arch. SHA-256 hash is computed lazily on first request and cached.

**Publishing:** Admin drops new binaries into a new version directory. No restart required — the release manager re-scans periodically.

### Server-Side: Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | /api/v1/releases/latest | Returns latest version + SHA-256 for the requesting agent's platform |
| GET | /api/v1/releases/download/{os}/{arch} | Streams the binary |

The heartbeat response also carries update info when a newer version is available, so agents don't need to poll the release endpoint separately.

### Agent-Side: Update Flow

1. Agent detects newer version (via heartbeat response or periodic release check every 24h).
2. Downloads new binary to a temp file in the same directory as the running executable (same-filesystem guarantees atomic rename).
3. Verifies SHA-256 against server-advertised hash. Rejects and removes on mismatch.
4. Platform-specific apply:

| Platform | Mechanism | Service sees |
|----------|-----------|-------------|
| Linux | `os.Rename` + `syscall.Exec` — process replaces itself in-place, same PID | No restart event (seamless) |
| macOS | Same as Linux (rename + exec) | No restart event |
| Windows | Writes `.new` file alongside current, exits cleanly; service wrapper promotes `.new` on next launch | Normal service restart |
| Docker | N/A — uses Watchtower image pulls | Container recreation |

### Safety Guarantees

- **SHA-256 verification:** Binary is rejected if hash doesn't match. Origin authentication is via the pinned TLS connection (no separate signature scheme needed).
- **Atomic replacement:** Same-filesystem rename ensures the binary is never in a half-written state.
- **Concurrent guard:** Only one update can be in progress at a time (mutex-protected). A second heartbeat arriving during download is a no-op.
- **Rollback (implicit):** If the new binary crashes within 60s of launch, the service wrapper (systemd/launchd) restarts — at which point the old binary is still the registered executable. Explicit rollback is not implemented (would require keeping a copy of the previous binary).
- **Infrequent checks:** 24h polling avoids hammering the server. Heartbeat-carried notifications provide faster propagation when the server pushes urgently.

### Docker Agents

Docker-deployed agents don't use the binary self-update path. Instead:
- Watchtower watches the container image tag.
- Admin pushes a new image (`make docker-agent` → pushes `walljm/jmw-agent:latest`).
- Watchtower pulls and recreates the container within its poll interval.
- No agent cooperation required — the container is replaced externally.

### Home Assistant Agents

HA add-on agents update through the Supervisor's add-on update mechanism. Home Assistant installs this repo as an add-on repository, reads `jmw-agent/config.yaml`, and pulls the prebuilt `docker.io/walljm/jmw-agent-ha:<version>` image. The add-on manifest version omits the binary tag's leading `v` (`2.3.0` in `config.yaml` for release tag `v2.3.0`). The release workflow validates that match, publishes the image, and Supervisor handles download and restart.

The in-process binary updater is disabled inside HA add-ons because `SUPERVISOR_TOKEN` means Supervisor owns the container lifecycle.
