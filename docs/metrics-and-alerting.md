# Metrics & Alerting

Time-series data collection, storage, aggregation, alert rule evaluation, and notification delivery. This subsystem is **separate from the observation pipeline** — metrics are numeric measurements over time, not identity/topology observations.

## Metrics vs. Observations

| Concern | Observation Pipeline | Metrics System |
|---------|---------------------|----------------|
| What | "Device X exists, has MAC Y, hostname Z" | "Agent A's CPU is 87% at time T" |
| Shape | Key-value facts about entities | Numeric time-series |
| Storage | Entity tables + bucketed observations | Snapshot tables with time-based indexing |
| Retention | Long-lived (entity state persists) | Rolling windows with rollups |
| Trigger | Entity state changes, UI views | Alert rule evaluation, dashboard graphs |

Metrics attach to **systems** (specifically, systems running our agent). They're not relevant for unmanaged devices — we can't collect CPU usage from a neighbor's printer.

---

## Collection

Agents collect system metrics on a configurable interval (default: 30 seconds) and batch-POST them to `POST /api/v1/agent/metrics`.

### Metric Categories

| Category | Fields | Source |
|----------|--------|--------|
| System | cpu_pct, mem_used_bytes, mem_total_bytes, load_1/5/15, uptime_seconds | /proc/stat, /proc/meminfo, sysctl |
| Disk | device, mountpoint, used_bytes, total_bytes, read_iops, write_iops, smart_health, temperature_c | /proc/diskstats, statfs, smartctl |
| Network Interface | interface_name, rx_bytes, tx_bytes, rx_packets, tx_packets, rx_errors, tx_errors | /proc/net/dev, /sys/class/net |

### Wire Format

```go
type MetricsRequest struct {
    AgentID   string
    Snapshots []MetricSnapshot
}

type MetricSnapshot struct {
    Timestamp  time.Time
    CPU        float64
    MemUsed    int64
    MemTotal   int64
    Load1      float64
    Load5      float64
    Load15     float64
    Uptime     int64
    Disks      []DiskSnapshot
    Interfaces []InterfaceSnapshot
}
```

---

## Storage

### Raw Snapshots

Six tables for different time-series categories. All follow the same lifecycle (raw → rollup tiers → prune).

**metric_snapshots** (system-level)
```sql
(agent_id, ts) PRIMARY KEY
cpu_pct, mem_used_bytes, mem_total_bytes, load_1, load_5, load_15, uptime_seconds
```

**disk_snapshots** (per-device, I/O + health)
```sql
(agent_id, ts, device) PRIMARY KEY
mountpoint, read_iops, write_iops, smart_health, temperature_c
```

**interface_snapshots** (per-NIC counters)
```sql
(agent_id, ts, interface_name) PRIMARY KEY
rx_bytes, tx_bytes, rx_packets, tx_packets, rx_errors, tx_errors
```

**filesystem_snapshots** (per-mount space usage)
```sql
(agent_id, ts, mountpoint) PRIMARY KEY
device, fs_type, total_bytes, used_bytes, free_bytes, inodes_used, inodes_free
```

**temperature_snapshots** (thermal sensors)
```sql
(agent_id, ts, sensor_name) PRIMARY KEY
sensor_type, celsius
```

**battery_snapshots** (laptops, UPS)
```sql
(agent_id, ts) PRIMARY KEY
design_capacity_wh, current_capacity_wh, health_pct, cycle_count, state, charge_pct
```

**processes_snapshot** (top-N processes)
```sql
(agent_id, ts, pid) PRIMARY KEY
name, user, cpu_pct, mem_pct, mem_bytes, cmd
```

See entity-model.md → Metric Snapshot Tables for full column documentation. Every snapshot table shares the same retention machinery below.

### Wire Efficiency for Snapshots

Per-cycle wire payloads carry **short stable keys** rather than full identity, per the policy in `agent-lifecycle.md` → Wire Efficiency. Concretely:

- Disk snapshots send `device_name` (e.g., `nvme0n1`), not full disk identity. Server joins `(agent_id, device_name)` to recover `disk_id` via the Identity Resolver.
- Interface snapshots send `iface_name` (e.g., `eth0`), not MAC. Server joins `(agent_id, iface_name)`.
- Filesystem snapshots send `mountpoint`, not `(device, fs_type, total_bytes)` every cycle.
- Temperature snapshots send `sensor_name` only — sensor_type is part of inventory.
- Process snapshots send `pid` + `name` only; full cmd is sent inline because it's transient.

The server-side ingest joins these against the most recent inventory; if the join fails (new device after inventory), the ingest stores the snapshot and replies with `If-Inventory-Stale` to trigger an inventory refresh on the next cycle. Identity hydration uses the shared Identity Resolver (see `data-pipeline.md` → Identity Resolver).

### Coalesced Transport

All snapshot categories share one POST per cycle (`POST /api/v1/agent/tick`). The agent does not open separate connections per category. The body is gzip-compressed and includes the agent's enabled-subsystems hash so the server can detect drift (see `agent-lifecycle.md` → Subsystem Registry Handshake).

### Retention & Rollups

| Tier | Granularity | Retention | Purpose |
|------|-------------|-----------|---------|
| Raw | 30-second intervals | 48 hours | Real-time dashboard, recent alerting |
| 5-minute rollup | 5-minute averages | 7 days | Short-term trending |
| Hourly rollup | 1-hour averages | 90 days | Capacity planning, historical graphs |
| Daily rollup | 1-day averages | 1 year | Long-term trends |

**Rollup process:** Background goroutine runs on a schedule:
1. Aggregate raw snapshots older than 48h into 5-minute buckets (AVG/MAX/MIN).
2. Delete raw rows after successful rollup.
3. Aggregate 5-minute buckets older than 7 days into hourly.
4. Aggregate hourly buckets older than 90 days into daily.

Rollups preserve MIN, MAX, AVG for each field so graphs can show both average load and peak spikes.

---

## Alert Rules

An alert rule defines a condition that, when sustained for a duration, fires an alert.

### Rule Schema

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| name | Human label ("High CPU on production servers") |
| metric_kind | Evaluator family: `numeric_snapshot` / `posture_bool` / `posture_count` / `posture_age` / `source_health` |
| metric_path | Within-kind identifier (see Evaluator Dispatch below) |
| operator | `>` / `<` / `>=` / `<=` / `==` / `!=` |
| threshold | Comparison value (numeric for most kinds; boolean for `posture_bool`) |
| duration_seconds | How long condition must be sustained before firing. Also acts as the resolve dampening window (see Anti-Flap). |
| target_kind | What to evaluate against: `agent` / `tag` / `source` / `service` / `disk` / `network` / `hardware` / `all` |
| target_id | Specific entity ID, tag name, or empty for `all`. Must resolve under `target_kind`. |
| severity | `info` / `warning` / `critical` |
| channel_id | FK → notification_channels (where to send alerts) |
| enabled | Boolean toggle |
| created_at | When rule was created |

### Evaluator Dispatch

The evaluator is a dispatch table keyed by `metric_kind`. Each kind knows how to translate a `metric_path` + scoped targets into a current-value query and a per-sample comparison.

| metric_kind | What it evaluates | Example `metric_path` | Source query (shape) |
|-------------|-------------------|----------------------|----------------------|
| `numeric_snapshot` | Time-series snapshot averaged over `duration_seconds` window | `snapshot.cpu_pct`, `snapshot.mem_pct`, `snapshot.disk_pct`, `snapshot.load_5`, `snapshot.temperature_c`, `snapshot.battery_charge_pct` | `SELECT AVG(col) FROM <snapshot_table> WHERE agent_id=? AND ts > now - duration` |
| `posture_bool` | Boolean fact on a posture table (alerts when current state matches threshold) | `posture.firewall.enabled`, `posture.secure_boot.enabled`, `posture.encrypted_volumes.all_encrypted` | `SELECT current_value FROM <posture_table> WHERE agent_id=?` |
| `posture_count` | Integer count from a posture table | `posture.failed_services.count`, `posture.pending_updates.security_count`, `posture.listening_ports.exposed_count` | `SELECT COUNT(*) FROM <posture_table> WHERE agent_id=? AND <filter>` |
| `posture_age` | Seconds since a posture timestamp (e.g., antivirus signature age, last successful backup) | `posture.antivirus.signature_age_seconds`, `posture.update_status.last_check_age_seconds` | `SELECT EXTRACT(EPOCH FROM (now - col)) FROM <posture_table> WHERE agent_id=?` |
| `source_health` | Pipeline source liveness — used for "my terrain poller is dead" style alerts | `source.last_success_age_seconds`, `source.consecutive_error_count` | `SELECT … FROM sources WHERE id=?` |

**Special case: `offline_minutes`.** Derived from `(now - agent.last_heartbeat_at).Minutes()`. Modeled as `metric_kind=numeric_snapshot`, `metric_path=agent.offline_minutes`, scoped via `target_kind=agent|tag|all`. No snapshot table query — the evaluator special-cases this path.

**Adding a new evaluator family** is a code change in one place (the dispatch table) plus a documented `metric_path` set. Adding a new path within an existing family requires no code change beyond surfacing it in the UI rule builder. The Foundation Critic verifies that every documented `metric_path` resolves to a real column or aggregation; the rule editor in the UI rejects unknown paths.

### Evaluation

The alert evaluator runs every 30 seconds (aligned with metric collection):

```
for each enabled rule:
    skip if any active maintenance window covers (rule.target_kind, rule.target_id)
    resolve target entities (by target_kind + target_id)
    for each target:
        current = evaluator_dispatch[rule.metric_kind].query(rule.metric_path, target, rule.duration_seconds)
        if current violates threshold for the full duration window:
            if no open firing exists → FIRE (create alert_firing, queue notification)
        else if current has been stable (non-violating) for the dampening window:
            if open firing exists → RESOLVE (set resolved_at, queue resolve notification)
```

**Second-writer invariants.** The alert evaluator is the only writer in the system that is not the data pipeline. See `data-pipeline.md` → Alert Evaluator as a Second Writer for the full contract. Summary:

- Reads anything (metric snapshots, posture tables, sources, agents) but writes only `alert_firings` and `events`.
- Append-only on firings; existing rows only mutated to set `resolved_at` and `notified`.
- Serializes with pipeline writes through SQLite's WAL writer lock. Evaluator transactions MUST stay short (< 100 ms) so it doesn't stall pipeline ingest.
- Does NOT call back into the pipeline. Events for fire/resolve are written directly to the `events` table.
- Idempotent — no in-memory firing state; truth is the `alert_firings` table.

### Anti-Flap

A rule that toggles state every few seconds creates notification storms and resolves that the operator has to manually validate. The evaluator applies symmetric dampening on both edges:

- **Fire edge:** Already handled by `duration_seconds` — the condition must violate for the entire window before a firing opens.
- **Resolve edge:** A firing does NOT resolve the instant the condition clears. The condition must stay cleared for `duration_seconds` before `resolved_at` is set. This prevents a single recovering sample from flapping a firing closed only to re-open on the next cycle.
- **Re-fire suppression:** If a rule resolves and re-fires for the same target within `5 × duration_seconds`, the new firing is marked `flapping=true` and the notification carries that flag. The UI shows flapping firings in a separate band so the operator can investigate the underlying instability instead of acknowledging individual cycles.

### Firing Schema

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| rule_id | FK → alert_rules |
| agent_id | Which agent triggered |
| started_at | When condition first sustained |
| resolved_at | When condition cleared (NULL = still firing) |
| notified | Boolean: was notification delivered? |
| notification_error | Error message if delivery failed |
| flapping | Boolean: set when re-fire occurred within `5 × duration_seconds` of last resolve |

---

## Maintenance Windows

When an operator is patching a NAS, rebooting a switch, or moving a server, every metric will momentarily look like an alert. A maintenance window is a scheduled period during which matching rules do not open new firings.

Schema and semantics: see `entity-model.md` → Maintenance Windows.

Evaluator behavior:

- Before evaluating any rule, the evaluator loads the set of active windows once per cycle and indexes them by `(scope_kind, scope_id)`.
- For each rule, the evaluator checks whether any active window covers the rule's `(target_kind, target_id)`. A window with `scope_kind=all` covers everything; a window with `scope_kind=tag` covers any rule whose targets share the tag; entity-level windows match exactly.
- During an active window, the evaluator still computes current state (so the dashboard shows truth) but does NOT open new firings.
- Already-open firings are not auto-resolved on window open — they continue to track condition state. If they would have resolved during the window, they resolve normally; if they would have flapped, the window mutes the notification only.
- Events are written for window-open and window-close so the audit trail is complete.

---

## Notification Channels

| Kind | Config fields | Delivery |
|------|--------------|----------|
| email | smtp_host, smtp_port, smtp_user, smtp_pass, from_addr, to_addrs | SMTP with TLS |
| webhook | url, method (POST/PUT), headers, body_template | HTTP request with template expansion |

Future channels (Discord, Pushover, Gotify, Slack) follow the same pattern — kind + config_json.

### Delivery

On alert fire or resolve:
1. Load channel config.
2. Render notification body with template variables (agent hostname, metric value, threshold, severity, timestamp).
3. Check the per-channel rate limit (see below). If suppressed, append to the channel's pending-summary queue and skip immediate delivery.
4. Attempt delivery with 3 retries, exponential backoff.
5. Record success/failure on the firing record.

### Rate Limiting

Each channel carries a `rate_limit_per_hour` (NULL = unlimited; see `entity-model.md` → Notification Channels). Within a rolling 60-minute window:

- If the channel has emitted fewer than `rate_limit_per_hour` notifications, the new notification is sent immediately.
- If the limit is reached, the notification is queued. At the end of the rolling window the channel emits a single coalesced summary ("15 alerts suppressed in the last hour — NAS-1 high CPU x9, switch-2 offline x6") and the queue resets.
- Critical-severity notifications bypass the rate limit. The rationale: rate limiting protects against flap storms on warning/info; a critical alert is allowed through because the operator wanted to be paged.

This is intentionally simple. A home-lab tool doesn't need per-rule notification budgets or hierarchical grouping — the channel-level limit handles 95% of the noise problem (a single misbehaving rule cannot spam more than its budget per hour).

---

## Relationship to Entity Model

Metrics are stored against **agent_id** (which maps to a System entity). The metrics system doesn't directly reference hardware/interface/observation entities — it's a parallel concern:

```
Agent ←→ System (entity model)
  │
  └── metric_snapshots (time-series)
  └── disk_snapshots
  └── interface_snapshots
  └── alert_firings (when thresholds breached)
```

Dashboard views join metrics with entity data to show "System X (hardware Y, on network Z) has high CPU." But the metrics tables themselves are simple — just agent_id + timestamp + values.

---

## Dashboard Queries

| View | Query pattern |
|------|--------------|
| Agent overview sparkline | Last 2h of raw metric_snapshots for agent, 1-per-minute sampling |
| Agent detail graphs | Raw for last 48h, 5-min rollup for last 7d, hourly for longer |
| Alert list | Open firings (resolved_at IS NULL) joined with rules + agents |
| Disk health summary | Latest disk_snapshot per (agent, device) — SMART status + usage |
| Network throughput | Latest interface_snapshots per (agent, interface) — delta rx/tx bytes |
