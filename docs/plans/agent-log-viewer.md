# Plan: Agent log viewer (on-demand journald/console pull)

> **Status:** PROPOSED — spec only, no code yet.
> **Trigger:** Boss wants a way to see what an agent has been logging (cache clears, collector
> errors, self-update activity) without SSHing into the host. Explicit constraints from
> conversation: pull the **existing** console/journald output rather than build a new logging
> pipeline; deliver it **on demand**; **do not** persist log content in the server's Postgres
> database.

## 1. Goal

Let an admin, from the Fleet UI, ask a specific agent "show me your recent logs" and see the
actual text a `journalctl -u jmw-agent` (native install) or a look at stdout (Docker) would show
— cache-clear events, collector/scanner failures, heartbeat failures, self-update activity —
without shelling into the host. No new structured log format, no log content in Postgres.

## 2. Non-goals / guardrails

- **Not a telemetry/observability platform.** No log aggregation across the fleet, no search
  index, no retention policy beyond "long enough for an admin to look at it once." One agent,
  one on-demand pull, one viewer.
- **No log content in Postgres.** Only a request timestamp (metadata, same shape as the existing
  `clear_trackers_requested_at` column) is persisted. The actual log text lives in an in-memory
  cache on the server process and is gone on restart or after a short TTL.
- **On-demand only, no continuous streaming/upload.** The agent does not push logs every cycle —
  only when an admin explicitly asks, mirroring the existing clear-cache request pattern
  (`ClearTrackersRequestedAt` in `HeartbeatConfig`).
- **Pull what's already being written, don't build a second logging pipeline.** The agent
  already logs through `Microsoft.Extensions.Logging` → `AddConsole()` (§7 covers a couple of
  gaps in that pipeline worth fixing as part of this work). This feature surfaces that existing
  output, it doesn't add a parallel structured-audit log inside the agent.
- **Latency bound = one heartbeat interval**, same as clear-cache and interval/collector-toggle
  changes today. There is no inbound channel to agents (they're behind NAT/firewalls in the
  common case) — everything is agent-initiated on its own poll cadence.

## 3. Current state (why this isn't trivial)

- `Program.cs` wires a single `AddConsole()` sink at `LogLevel.Information`, no file/journald-aware
  sink. Every log line an operator can ever see today is whatever the process wrote to stdout in
  its current run — there is no "last night's crash" unless someone was tailing the console (or,
  on the native systemd install, journald already captured it independently of the app).
- **Two deploy targets behave differently:**
  - **Native systemd** (`deploy/systemd/jmw-agent.service`, unit `jmw-agent`, runs as root):
    journald captures stdout/stderr automatically, plus systemd's own lifecycle events (start,
    restart, OOM-kill, non-zero exit) that the app itself never sees. `journalctl -u jmw-agent`
    is the authoritative source here — reachable from inside the agent process itself since it
    runs as root.
  - **Docker** (`deploy/docker/`, NAS/appliance targets): no systemd inside the container, and
    the agent process cannot reach the *host's* `docker logs` output for its own container
    (that lives in the Docker daemon's log driver, outside the container's namespace, and would
    require bind-mounting `docker.sock` with write access just to read its own logs — too much
    privilege for what this buys). `journalctl` is a dead end here.
- Net: there is no single command that works on every deployment target. The design below
  handles this with two capture sources and a fallback, not by picking one and hand-waving the
  other (see §4.1).

## 4. Design

### 4.1 Agent-side capture — dual source

**Primary source — `journalctl` (native systemd only).** Detected at request time by checking for
both `/run/systemd/system` (proof we're under a systemd instance) and the `journalctl` binary on
PATH. When available:

```
journalctl -u jmw-agent --no-pager -n <lines> -o short-iso
```

This is the real ask — it includes systemd's own lifecycle lines (restart, OOM-kill, non-zero
exit) that the app can never log about itself, which is exactly the kind of thing worth auditing
("did it crash-loop last night").

**Fallback source — in-process ring buffer (everywhere else: Docker, dev runs, macOS, Windows).**
A small `ILoggerProvider` (`RingBufferLoggerProvider`) registered alongside `AddConsole()` in
`Program.cs`, holding the last N formatted lines (bounded by count **and** total bytes, e.g. 2000
lines / 256 KB, whichever hits first — a ring buffer, not an unbounded list). This is *exactly*
the same content that would appear in `docker logs` for that container, captured from inside the
process instead of requiring `docker.sock` access. Always-on, negligible overhead (a lock-free
ring of strings), zero external dependency.

Capture logic: try `journalctl` first; if the systemd markers aren't present or the command fails
(permission denied, binary missing), fall back to the ring buffer. Always label which source
produced the payload so the UI can show "journald" vs "in-process buffer (no journald on this
host)" — an admin reading a truncated in-process buffer needs to know it doesn't include
anything before the process last started, unlike journald.

Each request is one **page**: a fixed line cap (default 500, selectable 200/500/1000 — no
"2000+" option; see §4.2 for how an admin reaches further back instead of raising this), plus a
byte ceiling (~128 KB) as a backstop against a single pathological line. This is a manual,
occasional pull, not a bulk export — no reason to ship megabytes over the wire or force the admin
to scroll a novel in one go.

**Paging older.** Every capture request carries an optional `before` token; the response carries
a `next_before_token` (null once the source is exhausted). Each source implements this
differently:

- **Ring buffer:** every appended entry gets a monotonically increasing `long Seq`. `before` is
  the `Seq` of the oldest line already shown; the next page is the `lines` entries immediately
  below it. `next_before_token` is null once `Seq` reaches the buffer's oldest surviving entry —
  which may mean "the process hasn't logged anything older," or may just mean "it did, but the
  ring already overwrote it." No way to tell those apart from inside the buffer, so the UI should
  say "no earlier logs available (may be outside the in-memory buffer's retention)" rather than
  implying it's confirmed history.
- **journald:** `before` is the `__CURSOR` of the oldest line already shown (captured via `-o
  json`, which includes `__CURSOR` per entry). The next page is fetched with
  `journalctl -u jmw-agent -r -n <lines+1> --cursor="<before>" -o json` — over-fetching by one and
  dropping the entry whose cursor matches `before` exactly, rather than relying on precise
  cursor-inclusivity semantics. `next_before_token` is null when the over-fetched batch comes back
  short (fewer than `lines+1` results), meaning journald has nothing older for this unit.

### 4.2 Request/fetch flow

Mirrors the existing clear-trackers pattern (`Agent.cs` `ApplyClearTrackersRequestIfNeeded`,
`AgentDetail.cshtml` "Clear agent cache" button) end to end:

1. Admin clicks **Fetch Logs** (new UI, §4.4) → `POST /api/v1/admin/agents/{id}/request-logs`
   `{ lines?: 500, before?: null }`. Server sets `agents.logs_requested_at = now()` (+ line count
   + the `before` token, opaque to the server — it just relays whatever the agent gave back last
   time), audit-logs `agent.request_logs` (existing `AuditLog` table — this is metadata, not log
   content, so it's fine there; see §2).
2. Server's assembled `HeartbeatConfig` (`AgentConfigAssembler.cs`) gains `LogsRequestedAt` /
   `LogsRequestedLines` / `LogsRequestedBefore`, alongside the existing `ClearTrackersRequestedAt`.
3. On its next heartbeat, the agent compares the server's `LogsRequestedAt` against a locally
   persisted marker (`_lastLogsUploadedAt`, same shape as `_lastTrackersClearedAt` — a file in
   `stateDir`). If newer, it captures logs per §4.1 (honoring `before` when present) and, on the
   same cycle, `POST`s them to a new agent-facing endpoint: `POST /api/v1/agent/logs` (bearer API
   key, same `AgentApiKeyMiddleware` as heartbeat/facts) with `{ requested_at, source:
   "journald"|"buffer", truncated, text, next_before_token }`. Updates its local marker
   regardless of upload success (don't retry-storm on a server hiccup — the admin can just click
   again).
4. Server's endpoint hands the payload to an in-memory cache service (§4.3) keyed by agent id.
   **No Postgres write for the text.**
5. Admin UI (already open, waiting) polls `GET /api/v1/admin/agents/{id}/logs` every ~5s until
   the cache has a bundle newer than the request, or gives up after ~2× the agent's heartbeat
   interval with "no response — is the agent online?" (same honesty the clear-cache toast already
   has: "takes effect on the agent's next heartbeat").

**Latency tradeoff on "Load older," stated plainly:** because there's no persistent/inbound
channel to an agent, clicking **Load older** is a brand-new round trip through steps 1–5 above —
not an instant client-side page flip. It waits for the agent's next heartbeat, same as the
initial fetch (up to the agent's configured heartbeat interval, default 5 min). This follows
directly from the "on-demand, no continuous streaming" constraint (§2): the only way to make
paging instant would be to have the agent upload a much bigger window up front "just in case,"
which contradicts "keep it capped at a reasonable number of lines." Worth confirming this
latency is acceptable — if not, the fix is a larger single fetch with server-side pagination of
an already-fetched blob, at the cost of a bigger one-time transfer.

### 4.3 Server-side in-memory cache (no DB)

A singleton `AgentLogCache` service:

```csharp
sealed record AgentLogBundle(
    DateTimeOffset RequestedAt,
    DateTimeOffset ReceivedAt,
    string Source,             // "journald" | "buffer"
    bool Truncated,
    string Text,
    string? NextBeforeToken    // null once the source has no older lines
);

sealed class AgentLogCache
{
    private readonly ConcurrentDictionary<Guid, AgentLogBundle> _bundles = new();
    // Set(agentId, bundle); TryGet(agentId, out bundle); periodic sweep evicts entries
    // older than a TTL (e.g. 15 min) and caps total entries (e.g. 200 agents) so a
    // long-idle admin tab / forgotten fleet doesn't grow this unbounded.
}
```

One bundle per agent, always the *most recently received page* — the cache does not stitch
pages together into agent-side history. Each "Load older" click overwrites the cached bundle with
the new page once it arrives; the UI (§4.4) is responsible for appending pages together for
display within one browser session, not the server.

Registered as a singleton, evicted by a lightweight periodic sweep (same `BackgroundService`
shape as `ReleaseRescanService`/`MetricPartitionService` — nothing new architecturally). Gone on
server restart, which is fine: this is a "look at it now" feature, not an archive.

### 4.4 Admin UI

New **Logs** tab on `AgentDetail.cshtml`, alongside Host Collectors / Discovery / Targets /
Settings (not folded into Settings — the content is fundamentally different: a scrollable text
blob, not a form). Contents:

- **Fetch Logs** button + a page-size selector (200 / 500 / 1000).
- Status line: "Requested Xs ago — waiting for the agent's next heartbeat" → "Received Ys ago
  from `journald`/`in-process buffer`" (+ "truncated" badge if applicable).
- A `<pre>` viewer (monospace, scrollable, `overflow: auto`, matching the artifact/dataviz
  scroll-containment convention already used elsewhere) with a **Copy** button. No syntax
  highlighting needed — it's log text. Pages accumulate in the viewer newest-first-at-top as
  older ones are loaded, so the admin reads a continuous scroll rather than swapping views.
- **Load older** button at the bottom of the viewer, disabled/hidden once `next_before_token` is
  null. Clicking it re-issues the request flow (§4.2) with that token and shows the same
  "waiting for next heartbeat" status as the initial fetch — it is **not instant**; see the
  latency note in §4.2. The button's label reflects this ("Load older (waits for next
  heartbeat)") so it doesn't read as a broken/slow click.
- Same polling pattern already used for Overview refresh (`pollOverview`, 15s) — a dedicated
  short-lived poll (~5s) only while a request is outstanding, stopping once a bundle lands or the
  give-up window passes.

### 4.5 Audit trail

The existing `audit_log` table (`AuditLog.WriteAsync`, already used for `agent.approve`,
`agent.clear_cache`, etc.) gets one more action: `agent.request_logs`, actor = the admin user,
`target_ref` = agent id. This *is* the "auditing" half of the ask — who pulled logs from which
agent and when — and it's exactly the kind of small, structured metadata this table already
holds. It does not carry the log text itself.

## 5. Data model / API changes

| Change | Where |
|---|---|
| `agents.logs_requested_at TIMESTAMPTZ`, `agents.logs_requested_lines INT`, `agents.logs_requested_before TEXT` | new migration `0085_agent_request_logs.sql`, same shape as `0070_agent_clear_trackers.sql` |
| `RequestLogs.sql` / `RequestLogsAsync` | `Data/Agents/`, mirrors `RequestClearTrackers.sql` |
| `GetAgentConfig.sql` gains the three columns | feeds `AgentConfigAssembler` |
| `HeartbeatConfig` gains `LogsRequestedAt`, `LogsRequestedLines`, `LogsRequestedBefore` | `src/Core/DeviceRegistration.cs`, alongside `ClearTrackersRequestedAt` |
| `POST /api/v1/agent/logs` — body includes `next_before_token` | new agent-facing endpoint, `AgentApiKeyMiddleware`-protected, same auth as facts/heartbeat |
| `POST /api/v1/admin/agents/{id}/request-logs` — body `{ lines, before? }` | `AgentsApi.cs`, `RbacPolicies.Admin`, audit-logs `agent.request_logs` |
| `GET /api/v1/admin/agents/{id}/logs` — response includes `next_before_token` | `AgentsApi.cs`, reads `AgentLogCache`, `RbacPolicies.Admin` |
| `AgentLogCache` singleton + eviction sweep | new `Ingest/` or `Infrastructure/` service, registered in `Program.cs` (server) |
| Agent: `_lastLogsUploadedAt` marker file, `Capture/RingBufferLoggerProvider` (entries carry `Seq`), `Capture/AgentLogCollector` (journalctl-or-buffer logic, handles `before`/cursor paging) | `src/Agent/` |
| `Agent.cs`: apply logs-request the same way as `ApplyClearTrackersRequestIfNeeded` | `SendHeartbeatAsync` |

## 6. Security / privacy considerations

- **Admin-only, same RBAC as every other agent-management action** — nothing new here.
- **Log content can leak operational detail** (endpoints, hostnames, IPs, error messages) to
  whoever can view it — already true of the Targets/Configuration tabs on the same page, so this
  doesn't raise the bar, but worth stating rather than assuming.
- **Pre-ship checklist: verify no `[LoggerMessage]` call site anywhere in `src/Agent` interpolates
  a secret** (SSH password, SNMP community string, API token, credential material). Spot-checked
  `Agent.cs`, `Updater.cs`, `NetworkDiscoveryCollector.cs` for this plan — all clean (log messages
  carry endpoints/names/exception types, never `Target.Credentials`) — but this needs a full pass
  across every collector's `LoggerMessage` definitions before this ships, since this feature is
  what turns "a stray secret in a log line" from theoretical into "one click away in the admin
  UI."
- **`journalctl` needs no new permission** on the native install — the unit already runs as root
  (documented in `AGENTS.md`/`jmw-agent.service` for collector coverage), which can already read
  the system journal.

## 7. Logging audit — findings

Scope: `src/Agent/**`. Overall the existing pattern is sound and doesn't need a rewrite:

**What's already sane:**
- All log call sites go through source-generated `[LoggerMessage]` partials (`AgentMessages` in
  `Agent.cs`, `UpdaterLog` in `Updater.cs`, `CaTrustLog`, `NetworkDiscoveryCollectorLog`, etc.) —
  consistent, allocation-free, no raw string interpolation into log calls anywhere.
- **Every collector/scanner run is wrapped centrally**, not left to each implementation to
  remember: `CollectOneLocalWithStatsAsync` (local collectors), `CollectDeviceWithStatsAsync`
  (device collectors), `RunScannerAsync` (network scanners), and the service-collector loop in
  `CollectServicesAsync` each catch, log via `AgentMessages`/`NetworkDiscoveryCollectorLog`, and
  still record a per-run stat (duration, error type) in the cycle summary the server stores. A
  collector that never calls `_logger` itself still gets its failures logged and surfaced in the
  Fleet UI's per-collector health column — this is *by design*, confirmed correct, not a gap.
- The ~34 bare `catch { }` blocks found across `Agent.cs` and several `Local`/`Network`
  collectors (`RebootHistoryCollector`, `GpuCollector`, `DiskCollector`, `OsCollector`,
  `UserCollector`, `BatteryCollector`, `SecurityCollector`, `HwInventoryCollector`,
  `GatewaySnmpArpScanner`, `SshBannerScanner`) were checked individually: every one guards a
  single **optional sub-probe** inside a collector that already returns a partial, successful
  result either way (e.g. "try to read this one optional sysfs file / registry key / run this one
  best-effort command; if it fails, this one fact is just absent"). These don't throw past the
  collector boundary, so the centralized wrapper above never sees them — that's correct: logging
  every "feature X doesn't exist on this host" at Debug would be noise on the vast majority of
  hosts that legitimately lack that subsystem (e.g. no GPU, no battery). No action needed.

**Real gaps found, worth fixing alongside this feature:**
1. **`AddConsole()` should be `AddSystemdConsole()` (or select between them) on the native
   install.** `AddConsole()` emits plain text with no syslog priority prefix; journald ingests it
   as undifferentiated priority. `AddSystemdConsole()` (same `Microsoft.Extensions.Logging.Console`
   package, already referenced) prefixes each line with the `<N>` syslog level journald expects,
   which makes `journalctl -u jmw-agent -p err` / `-p warning` actually filter correctly instead
   of returning everything or nothing. This directly affects the value of §4.1's `journalctl`
   path — worth fixing as part of this work, not after. Detection: same `/run/systemd/system`
   check as §4.1; fall back to `AddSimpleConsole()` for Docker/dev/Windows/macOS.
2. **One raw `Console.Error.WriteLine` bypasses the logger** (`Program.cs`, the
   `UpdatePublicKey.Value` empty-key warning) even though `AgentLog.Factory` is already
   initialized two lines above it. Low severity — it still lands on stderr and gets captured by
   whatever's collecting the process's output either way — but it's the one line in the whole
   agent that doesn't go through `[LoggerMessage]`, and there's no reason for it not to now that
   this feature makes "what did the agent log" a first-class question. Fix: route it through
   `AgentLog.CreateLogger<Program>()` (or a small `AgentMessages.UpdatePublicKeyMissing` entry)
   like everything else.
3. **No way to raise verbosity without a redeploy.** `SetMinimumLevel(LogLevel.Information)` is
   hardcoded; there's no Debug-level detail available even when actively troubleshooting via this
   new log viewer. Not required for this feature to ship, but worth a follow-up: a
   `LogLevel` field in the same `HeartbeatConfig` block already used for interval/collector
   overrides, so "put this one agent in Debug for the next hour" doesn't need a binary swap.
   Flagging as a **separate, smaller follow-up** — happy to spec it if wanted, not bundling it
   into this plan's scope.

## 8. Open questions

1. **Docker fallback: confirmed direction, pending final go-ahead.** The in-process ring buffer
   is the only option that avoids mounting `/var/run/docker.sock` (which would grant the agent
   control over every container on the host just to read its own output). Cost: it only covers
   logs since the current process/container start — a crash-loop's earlier runs aren't visible
   through this feature, only via `docker logs` from the host directly. Recommend proceeding on
   this basis; flag if the `docker.sock` tradeoff is preferred instead for full crash-loop
   history.
2. **"Load older" latency.** Resolved to a fixed page size (200/500/1000 lines) plus a
   cursor/sequence-based `before` token so an admin can page backward — but each older page is a
   fresh heartbeat round trip (§4.2), not instant, given the on-demand/no-DB/no-streaming
   constraints. Flag if that latency is a problem in practice; the alternative (bigger single
   fetch, paginated client-side) trades it for a larger one-time transfer.
3. New "Logs" tab (not folded into Settings) — going with this per the design above.
