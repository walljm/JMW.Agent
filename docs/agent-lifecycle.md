# Agent Lifecycle

The agent is a single Go binary that runs on monitored hosts. It collects local system data, discovers network neighbors, and reports everything to the central server. This document covers the agent's operational lifecycle — from initial deployment through ongoing operation.

## Identity

Each agent has a persistent identity generated on first run:

| Field | Description |
|-------|-------------|
| ID | 16-byte random hex string, stored in `agent-data/agent.id` |
| Hostname | OS hostname at registration time (updated on each heartbeat) |
| OS | Runtime GOOS (linux, darwin, windows) |
| Arch | Runtime GOARCH (amd64, arm64) |
| Version | Build-time embedded version string |

**Identity persistence:** The agent ID file survives restarts, upgrades, and reconfigurations. It's the one piece of state the agent maintains. If the file is deleted, the agent registers as a new instance.

**In the entity model:** An agent is simultaneously:
- A row in the `agents` table (operational metadata, approval status)
- A **System** entity (the bare-metal OS it runs on)
- Indirectly, a **Hardware** entity (the chassis hosting that system)
- An **observation source** (feeds discovery + inventory + metrics into the pipeline)

---

## Registration Flow

```
Agent (first boot)                    Server
      │                                  │
      │── POST /api/v1/agent/register ──▶│
      │   {id, hostname, os, arch, psk?} │
      │                                  │
      │◀── 202 Accepted ────────────────│  (status=pending, unless PSK matches)
      │    or 200 OK (if PSK approved)   │
      │                                  │
      │── POST /api/v1/agent/heartbeat ─▶│  (polls every 30s)
      │   {id}                           │
      │                                  │
      │◀── {status: "pending"} ─────────│  (agent waits)
      │    or {status: "approved"}       │
      │                                  │
      │  (once approved, begins normal operation)
      │                                  │
```

### Pre-Shared Key (PSK) Auto-Approval

If the server has a PSK configured and the agent provides the matching key at registration, the agent is immediately approved without admin intervention. Useful for automated deployments.

### Pending Expiration

Pending registrations that are never approved expire after a configurable timeout (default: 72 hours). Expired pending agents are cleaned up automatically.

### Admin Notification

When a new agent registers (pending status), the server fires a notification to configured channels so the admin knows to approve or reject.

---

## Operational States

```
┌──────────┐   register    ┌─────────┐   admin approves   ┌──────────┐
│   New    │──────────────▶│ Pending │───────────────────▶│ Approved │
└──────────┘               └─────────┘                    └─────┬────┘
                                │                               │
                                │ expires / admin rejects        │ admin deregisters
                                ▼                               ▼
                           ┌──────────┐                   ┌──────────────┐
                           │ Expired  │                   │ Deregistered │
                           └──────────┘                   └──────────────┘
```

- **Pending:** Agent heartbeats are accepted but discovery/inventory/metrics are rejected (403).
- **Approved:** Full operation. All subsystems active.
- **Deregistered:** Agent is tombstoned. If it contacts the server, it's told to stop. Device/metric data is retained (attributed to this agent) but no new data is accepted.

---

## Subsystems

Each agent runs configurable subsystems. The set of *valid* subsystem identifiers lives in the server's `subsystems` registry table (see entity-model.md → Subsystems). The set of *enabled* subsystems for a given agent is recorded in `agent_subsystems` (junction table), not as a JSON array on the Agent row.

| Subsystem | What it does | Default |
|-----------|-------------|---------|
| metrics | Collects CPU, memory, load, uptime | enabled |
| discovery | Scans local network for neighbors | enabled |
| disk | Collects disk usage + SMART health | enabled |
| docker | Queries Docker/Podman runtime | enabled (if socket exists) |
| latency | Placeholder for future ping monitoring | disabled |
| smart | Deep SMART attribute collection (requires privileges) | enabled on Linux |

Subsystems are toggled in the agent's TOML config:
```toml
[subsystems]
metrics = true
discovery = true
disk = true
docker = true
smart = true
```

### Subsystem Registry Handshake

The set of subsystems each agent runs is reconciled with the server on every registration and on every config-driven restart. The handshake makes the registry — not the wire payload — the source of truth for what subsystems exist.

**On agent registration / restart:**

1. Agent reads its local `[subsystems]` config and computes its enabled-subsystem set.
2. Agent computes `subsystems_hash = SHA-256(sorted comma-separated names)`.
3. Agent includes both the full set AND `subsystems_hash` in its registration request (and in its first `/tick` after a config-change-restart).
4. Server validates each name against the `subsystems` registry table:
   - **Known + active subsystem** → upsert `agent_subsystems(agent_id, subsystem_name, enabled=true)`.
   - **Known + deprecated subsystem** → accept, but log a warning event and surface in the agent's "needs attention" badge.
   - **Unknown subsystem** → accept the registration (forward-compat), but write a row to `agent_unknown_sections` keyed by `(agent_id, "subsystem:" + name)`. The Foundation Critic flags any such row older than one release cycle — that means the server is running an older version than the agent.
5. Server returns the canonical, server-side enabled set + the registry's current schema version so the agent can detect mismatches.

**On subsequent ticks:**

The agent sends only `subsystems_hash` in the `/tick` payload (~32 bytes). The server compares to the most recently confirmed hash for that agent. On mismatch, the server's `/tick` response carries `If-Subsystems-Stale: true` and the agent re-sends the full set on its next request. This keeps the per-tick wire cost flat regardless of subsystem count.

**Server-driven subsystem changes:**

The UI does NOT modify `agent_subsystems` directly. The truth is the agent's local config — pushing changes from the server is a future feature requiring its own design (command channel + acknowledgment). Today, an operator wanting to disable a subsystem edits the agent's TOML and restarts it. The handshake then reconciles automatically.

**Why a registry, not a free-text field:**

- New subsystems can be rolled out gradually: introduced in the registry, agents pick them up after upgrade, server queries (alerts, dashboards) can filter on `subsystem IS active`.
- Deprecated subsystems remain known so historical data still resolves their names without a code lookup.
- The registry rows carry an `expected_cadence_seconds` value the server uses to detect "subsystem went silent" alerts (data freshness, not just heartbeat).

---

## Communication Schedule

| Endpoint | Interval | Payload size (compressed) | Purpose |
|----------|----------|---------------------------|---------|
| POST /api/v1/agent/tick | 30 seconds | 0.5–3 KB | Combined heartbeat + metrics + discoveries (one round-trip per cycle) |
| POST /api/v1/agent/inventory | 24 hours | 2–25 KB | Full system state snapshot (per-section diffs allowed) |
| GET /api/v1/releases/latest | 24 hours | ~100 bytes | Check for new version |

All communication is initiated by the agent (outbound HTTPS). The server never connects to agents. This means agents behind NAT/firewalls work without port forwarding.

The single `/tick` endpoint replaces three separate per-cycle calls (heartbeat, metrics, discoveries) so each 30-second cycle is one request, one TLS frame, one handler dispatch. Sub-payloads inside the tick may be omitted when unchanged (see Wire Efficiency below).

---

## Transport

### TLS

- Agent connects to server over HTTPS.
- On first connection, the agent pins the server's certificate fingerprint (SHA-256 of the cert).
- Subsequent connections reject certificate changes (trust-on-first-use model).
- The pinned fingerprint is stored in `agent-data/`.
- If the server cert legitimately rotates, the agent must be reconfigured (or the pin file deleted for re-pinning).

### Authentication

- Every request includes the agent ID in the request body (not a header token — simpler, no bearer management).
- The server validates that the agent ID exists and is in an approved state.
- PSK is only used during registration, not ongoing communication.

### Retry & Resilience

- If the server is unreachable, the agent retries with exponential backoff (30s → 1m → 2m → 5m max).
- Collected data is buffered locally (bounded queue) and flushed on reconnection.
- Discovery and metrics continue collecting locally even when disconnected — they're only reported once connectivity returns.
- The agent never crashes due to server unavailability.

---

## Wire Efficiency

Agents report on slow-changing or sparsely-changing state at fast cadences. The wire format and reporting policy are designed to keep per-cycle bandwidth small without sacrificing freshness or robustness.

### Principles

1. **Compress everything.** All agent → server bodies are gzip-encoded (`Content-Encoding: gzip`). Responses likewise (`Accept-Encoding: gzip`). JSON payloads compress 5–10× because of repeated keys and structural sugar.
2. **Send identity once.** Stable identifiers (MAC, disk serial, interface name) are sent on inventory and on first metric snapshot after registration. Subsequent per-cycle snapshots reference entities by short stable key only — they do not re-ship MACs, IPs, mountpoints, or filesystem types that the server already has.
3. **Hash-skip unchanged payloads.** For sub-payloads that change rarely (discovery TXT/probes, inventory subsections), the agent computes a canonical content hash and includes it in the request. If the server already has that hash on record, the agent omits the body. The server returns a "stale, please resend" hint when its copy is missing or evicted.
4. **Delta over full state when cheap.** Discovery sightings prefer to ship only new MACs and entries whose `(Services, TXT, Probes, HostnameSources)` differ from the prior submission. A periodic full re-baseline (e.g., every 30 minutes) recovers from drift and from server-side eviction.
5. **Coalesce requests per cycle.** One `/tick` POST per 30-second interval carries heartbeat, metrics, and discovery delta (when present). Inventory remains a separate endpoint because of its size and cadence.
6. **Bound payload size.** Lists with unbounded cardinality (processes, packages, listening ports, services) are capped server-aware: processes top-N by CPU/mem, services only abnormal, packages opt-in. Caps are documented per field in `entity-model.md`.
7. **Omit empty.** All optional fields are `omitempty` so zero values don't occupy wire bytes.

### Sub-payload Cadence

| Sub-payload | Carried in | Cadence | Skip rule |
|-------------|-----------|---------|-----------|
| Heartbeat (liveness + version + config rev) | tick | every cycle | never skipped (it is the liveness signal) |
| Metric snapshot (CPU, mem, load, uptime) | tick | every cycle | never skipped |
| Per-disk snapshot (used/total) | tick | every cycle | omitted for disks whose used delta is below threshold and last sample was < N cycles ago |
| Per-interface snapshot (rx/tx counters, link state) | tick | every cycle | omitted for interfaces with no traffic since last sample |
| Discovery delta | tick | every cycle | omitted when hash matches prior submission and no new MACs |
| Discovery full re-baseline | tick | every 30 cycles (~15 min) | replaces delta; never skipped on its scheduled cycle |
| Inventory full | inventory | every 24h | per-section hash-skip: subsections (packages, processes, services, security, hassio) whose hash matches server's last copy are omitted |

### Identity Resolution

Per-cycle snapshots reference entities by short stable keys, not by the full identity object:

- **Disks:** referenced by `device_name` (e.g. `sda`, `nvme0n1`). Server joins to the disk entity via `(agent_id, device_name)`. Disk model/serial/size/type ride only on inventory.
- **Interfaces:** referenced by `iface_name` (e.g. `eth0`, `en0`). Server joins via `(agent_id, iface_name)`. MAC, IP addresses, MTU, link speed ride on inventory and on discovery, not on metrics.
- **Snapshots without a known key:** server treats the unknown-key reference as a hint to request inventory on next response (an `If-Inventory-Stale` field in the tick response).

### Counter Semantics

Network and disk byte counters are sent as **cumulative values** (rx_bytes, tx_bytes since boot/interface up). The server computes deltas. Rationale:

- Robust across dropped samples — a missed cycle does not lose throughput data, only resolution.
- Recoverable across agent restarts — the server records reset events (counter went backwards) and resumes.
- The cost (larger numbers in JSON) is fully absorbed by gzip.

### Multi-Agent Discovery Overlap

When multiple agents observe the same network segment, all of them ship sightings for the same MACs. The pipeline dedupes correctly server-side, but the wire and ingest cost is real. Mitigations, in order of complexity:

1. **Hash-skip handles the common case.** Two agents on the same LAN that scan the same devices will produce near-identical sighting sets; both compute the same hash on most cycles and both skip the body.
2. **Optional per-network scan leadership.** Agents on the same `gateway_mac` may elect a leader (lowest agent ID) that performs the heavy scan; others run a reduced scan and rely on the leader's report. Disabled by default; enabled per-network via config when overlap cost matters.

### Compression Is Not Optional

Every architectural payload-size estimate in this document assumes gzip is enabled end-to-end. A deployment that disables compression will see 5–10× the bandwidth and storage of access logs, but correctness is unaffected. Compression is a wire-format concern; it does not appear in the entity model or pipeline stages.

---

## Auto-Update

Agents can self-update when the server announces a newer version.

### Flow

```
Agent                                     Server
  │                                          │
  │── GET /api/v1/releases/latest ─────────▶│
  │                                          │
  │◀── {version: "1.5.0", sha256: "abc..."} │
  │                                          │
  │  (if version > current version)          │
  │                                          │
  │── GET /api/v1/releases/download/{os}/{arch} ──▶│
  │                                          │
  │◀── binary blob ─────────────────────────│
  │                                          │
  │  1. Verify SHA-256 matches               │
  │  2. Write to temp file                   │
  │  3. Replace self (atomic rename)         │
  │  4. Re-exec (Unix) or exit for          │
  │     service wrapper restart (Windows)    │
  │                                          │
```

### Server-Side Release Management

- Server scans a `releases/` directory for binaries named `jmw-agent-{os}-{arch}-v{version}`.
- Latest version is determined by semver sorting.
- SHA-256 hash is computed on demand.
- No automatic publishing — admin places new binaries in the release directory.

### Safety

- SHA-256 verification before replacement (no signature verification currently — binary is served over pinned TLS which provides origin authentication).
- Agent keeps the previous binary as a rollback target.
- If the new binary fails to start (crashes within 60s of launch), the service wrapper (systemd/launchd) restarts with the old binary still in place.
- Update checks are infrequent (every 24h) to avoid hammering the server.

---

## Configuration

Agent configuration via TOML file (`agent.toml`):

```toml
# Server connection
server_url = "https://192.168.1.100:8443"
psk = "optional-pre-shared-key"

# Subsystem toggles
[subsystems]
metrics = true
discovery = true
disk = true
docker = true
smart = true

# Collection intervals (override defaults)
[intervals]
metrics = "30s"
discovery = "5m"
inventory = "24h"

# Discovery tuning
[discovery]
excluded_interfaces = ["docker0", "br-*", "veth*"]
```

**Layered config resolution:** defaults → config file → environment variables → CLI flags. Missing config file is not an error (defaults are sufficient for basic operation).

---

## Deployment Models

| Platform | Installation | Service management | Update mechanism |
|----------|-------------|-------------------|-----------------|
| Linux (systemd) | Binary + unit file | systemd restart | Auto-update via re-exec |
| macOS (launchd) | Binary + plist | launchd restart | Auto-update via re-exec |
| Windows | Binary + service wrapper | Windows Service restart | Auto-update via exit (wrapper restarts) |
| Docker | Container image | Docker/Watchtower | New image tag (Watchtower auto-pulls) |
| Home Assistant | Addon | Supervisor | Addon update mechanism |

### Docker-Specific Concerns

When the agent runs in Docker, it needs host filesystem access for system metrics:
- `/host` mount (read-only) for `/proc`, `/sys`
- `JMW_HOST_ROOT=/host` environment variable
- `--privileged` for SMART data (raw block device access)
- `--network=host --pid=host` for accurate network/process visibility
- All host filesystem reads use `hostfs.Path()` wrapper

---

## Agent as Entity

In the unified entity model, an approved agent creates:

1. **Hardware entity** — identity_source=agent, confidence=high, serial from DMI
2. **System entity** — type=bare-metal, agent_id set, lifecycle=persistent
3. **Interface entities** — one per reported NIC, system_id linked to agent's system
4. **Service entity** — the agent itself is a monitoring service running on the system
5. **Child System entities** — one per Docker container (type=container, host_system_id → agent's system)

All of these are created/maintained through the standard pipeline when inventory reports are processed. The agent doesn't get special database access — its inventory is just another observation source (albeit the highest-priority one for its own system).
