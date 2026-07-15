# JMW Discovery

A home/SMB network discovery + inventory system written in **C# / .NET 10**.

A single ASP.NET Core server hosts the dashboard + API and stores everything in
**PostgreSQL**. Lightweight agents run on each host, stream host inventory back
over HTTPS as *facts*, and double as network sensors (ARP + active protocol
probes) that report every other device on their subnet. Devices that can't run
an agent (IoT, printers, routers) surface automatically as "discovered" entries.

> Earlier revisions of this repo were written in Go. That is gone — everything
> is C# now. Ignore any Go/TOML/`make` references you find in stale artifacts.

## Layout

```
src/Core/         JMW.Discovery.Core   — shared model: Fact, Fingerprint, analysis/normalizers
src/Agent/        JMW.Discovery.Agent  — collectors, network scanners, transport, agent runtime
  Collection/     collector interfaces + shared infra (contexts, DeviceIdentity, server client, Updater)
    Local/        ILocalCollector    — read the host directly (*Collector)
    Device/       IDeviceCollector + IServiceCollector implementations, one target model —
                  SSH/SNMP/BACnet/Modbus/Google Wifi (device) and Technitium/Home Assistant
                  (service) collectors all live here; "device vs service" is an interface-level
                  distinction now, not a folder-level one
    Network/      INetworkScanner    — broadcast/probe subnets to find unknown devices (*Scanner)
src/Server.Web/   JMW.Discovery.Server — ASP.NET Core: API, Razor Pages dashboard, ingest, projections
  Api/            minimal-API endpoints (agent-facing + admin + reporting), grouped by area
  Auth/           cookie sessions, bootstrap, RBAC
  Ingest/         FactRepository (facts_history), ProjectionRouter + GenericProjection (proj_* tables),
                  DeviceRegistry (fingerprint→device resolution), DiscoveryMaterializer
  Data/           SQL queries (see "Database queries" below) + Migrations/
  Pages/          Razor Pages dashboard
src/Tools/UpdateSign/ Signs agent release binaries for self-update (see "Agent self-update" below)
test/Unit/        JMW.Discovery.UnitTests
test/Integration/ JMW.Discovery.IntegrationTests  (needs a live Postgres)
lib/              ITPIE.Database.* (typed-SQL source generator + abstractions), ITPIE.Migrations
JMW.Discovery.slnx  solution file
```

## Build and test

```sh
dotnet build JMW.Discovery.slnx -c Release          # whole solution
dotnet test test/Unit/JMW.Discovery.UnitTests.csproj
dotnet test test/Integration/JMW.Discovery.IntegrationTests.csproj   # requires Postgres
```

The build is strict: `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`,
`Nullable=enable`. Fix warnings; don't suppress. Cross-build the agent for Linux
before deploying (see "Deploy").

## Run locally (docker-compose)

Copy `.env.example` to `.env` and fill in `JMW_DB_PASSWORD` and `JMW_API_KEY_SECRET` (each:
`openssl rand -hex 32`) — compose reads `.env` automatically and refuses to start without them.
`JMW_API_KEY_SECRET` must be at least 32 bytes; the server also refuses to boot if it's short or
left at a known placeholder value.

`docker compose up -d --build` starts:

- **postgres** — `postgres:17-alpine`, exposed on host **:5433** (db `jmwfacts`, schema `jmwdiscovery`).
- **server** — built from `src/Server.Web/Dockerfile`, listening on host **:8090** (`0.0.0.0`, so it's reachable over Tailscale/LAN).

Then:

1. Visit `http://localhost:8090/Bootstrap` and create the admin user (first-boot only; the page redirects to `/` once an admin exists).
2. Log in at `http://localhost:8090/Login`.
3. Run an agent pointed at the server; approve it under the Agents admin page (or `UPDATE jmwdiscovery.agents SET status='approved'` for a quick test).

Required server env (set in `docker-compose.yml`, sourced from `.env`): `JMW_DB_CONNECTION`,
`JMW_KEY_RING_PATH`, `JMW_API_KEY_SECRET`, `ASPNETCORE_URLS`.

## Agent

Config is **JSON** (`dev-agent.json` is the dev sample). Key fields: `server_url`,
`name`, `zone`, `interval` (seconds), `targets` (one unified list — each entry is a
device, e.g. SSH/SNMP/BACnet/Modbus/Google Wifi, or a service, e.g. Technitium
DNS/Home Assistant, matched to a collector by `collector_type`; `endpoint` is a bare
host for device-style entries or a full URL for service-style ones). The agent
stores its identity + API key under `JMW_AGENT_STATE_DIR` (default `/var/lib/jmw-agent`).

`server_url` must be `https://` — the heartbeat response carries decrypted device credentials
and every request carries this agent's bearer API key. A plain-`http://` `server_url` (e.g. the
local docker-compose server) is rejected at startup unless `"allow_insecure_http": true` is also
set, which is for lab/dev use only.

Lifecycle: register → server marks it `pending` → admin approves → agent posts
fact batches every cycle (delta-tracked: only changed facts are sent).

Publish a self-contained Linux binary:

```sh
dotnet publish src/Agent/JMW.Discovery.Agent.csproj -r linux-x64 -c Release \
  /p:PublishSingleFile=true /p:SelfContained=true -o out/agent-linux-x64
```

Run as **root** for full coverage — the docker/ports/disk/ARP collectors and raw-socket
scanners need privilege; unprivileged runs fall back to degraded/passive mode.

Native systemd install: binary at `/opt/jmw/bin/JMW.Discovery.Agent`, config at
`/etc/jmw/agent.json`, unit from `deploy/systemd/jmw-agent.service`. That unit uses
`Restart=always`, not `on-failure` — a successful self-update exits 0 after spawning the
new binary, and since the spawned child is in the same cgroup, systemd's default
`KillMode=control-group` reaps it right after; `on-failure` would then never restart the
unit (exit code 0 isn't a failure), leaving the agent permanently dead.

### Agent self-update

The agent can update itself from a binary the server offers over the heartbeat
(`Agent.cs`'s `TryApplyUpdateAsync` → `Collection/Updater.cs`). The server only
offers an update when all of these hold, so a misconfigured or unsigned release
never reaches the fleet:

1. `JMW_RELEASES_DIR` is set on the server and points at a populated releases directory.
2. A binary for the requesting agent's `(os, arch)` exists under a **clean semver**
   version directory (`vX.Y.Z`, no prerelease/build suffix — `ReleaseManager` ignores
   anything else, so a dev/dirty drop-in can't accidentally flap the fleet).
3. That binary has a `.sig` sidecar (see below) — an unsigned entry is indexed for
   visibility but never offered, since `Updater` rejects an empty signature anyway.
4. Its version is strictly newer than the version the agent reports on heartbeat.

Releases directory layout (`ReleaseManager.cs` scans it, refreshed every 2 minutes
by `ReleaseRescanService`):

```
$JMW_RELEASES_DIR/
  v1.3.0/
    jmw-agent-linux-x64
    jmw-agent-linux-x64.sig
    jmw-agent-linux-arm64
    jmw-agent-linux-arm64.sig
    jmw-agent-macos-x64
    jmw-agent-macos-x64.sig
    jmw-agent-windows-x64.exe
    jmw-agent-windows-x64.exe.sig
  v1.4.0/
    ...
```

Filenames must match `jmw-agent-<os>-<arch>[.exe]` using the same os/arch strings
the agent reports at registration (`linux`/`macos`/`windows`, `x64`/`arm64`/...).

**Signing a release** with the `src/Tools/UpdateSign` CLI (thin wrapper around the
shared `AgentUpdateSigning` helper in `src/Core` — the same code `Updater.cs` uses
to verify, so the signer and verifier can never drift):

```sh
# One-time: generate the fleet's signing keypair. Keep the private key secret;
# paste the printed public key into src/Agent/Collection/UpdatePublicKey.cs
# (Value) and rebuild the agent before shipping any signed release.
dotnet run --project src/Tools/UpdateSign -- generate-key --out update-signing.pem

# Per release: sign each published binary. Writes <binary>.sig next to it.
dotnet run --project src/Tools/UpdateSign -- sign \
  --key update-signing.pem --version v1.4.0 \
  out/agent-linux-x64/jmw-agent-linux-x64 out/agent-linux-arm64/jmw-agent-linux-arm64 ...
```

Rotating the key: generate a new pair, bake the new public key into
`UpdatePublicKey.Value`, ship that binary as the new baseline, then sign all
future releases with the new private key — a binary built with the old key
won't accept anything signed by the new one, so the fleet must already be on
a build carrying the new public key before you cut over.

### Docker deployment

For devices where a native install isn't practical (NAS appliances, anything
without a package manager/systemd), the agent also ships as a container:
`deploy/docker/Dockerfile.agent`, published multi-arch to Docker Hub
(`walljm/jmw-agent:latest` / `:vX.Y.Z`) by the `publish-docker` job in
`.github/workflows/release.yml`. Full run command, the `--uts=host
--network=host --pid=host` rationale, and the Watchtower-based auto-update
setup (containers can't use the native self-update above — the binary lives
in a read-only image layer) are in `deploy/docker/README.md`.

### Google Wifi collector

`GoogleWifiCollector` is a normal `IDeviceCollector` that reaches a Google Wifi /
Nest Wifi (OnHub) access point through its **local, unauthenticated HTTP API**
(`http://<ap-ip>/api/v1/`) — NOT the Google cloud API (Google retired the
`programmatic_auth` endpoint, so cloud refresh tokens can no longer be obtained).
It identifies the AP as a router device and emits each connected client station as
a `Device[].Discovered[]` neighbor fact keyed by IP — the same shape the SNMP/ARP
collectors produce — so clients flow through the existing projection +
`DiscoveryMaterializer` pipeline and appear as `discovered` devices.

**No credentials.** Configure it as a target in `agent.json`:

```jsonc
{
  "endpoint": "192.168.1.1",   // the AP's own IP
  "collector_type": "google-wifi"
}
```

Or from the admin UI: the agent's detail page → **Targets** tab → **+ Add Target**,
collector type **`google-wifi`**, endpoint = the AP IP. (There is no `google-wifi`
credential type.)

How it works (`Collection/Device/OnHub/`):

- `OnHubClient` GETs `/api/v1/diagnostic-report` (header `Host: localhost`; the body
  is a gzipped protobuf, ~2.8 MB and slow — the client allows a 4-minute timeout)
  and the small `/api/v1/status` JSON. GET-only; five endpoints exist, all else 404s.
- The report message is generated by `Grpc.Tools` from `diagnosticreport.proto`
  (ported from `../googlewifionhub`, with field 15 corrected to `bytes` and the
  missing fields 19 and 21 added — see the `.proto` comments), into `obj/` so it
  stays out of the strict analyzers. We consume fields 9 (`commandOutputs`), 16
  (`networkState`), and 21 (a clean 128-bit hex per-unit id). Field 21 is the device fingerprint
  (`FingerprintType.GoogleWifiDeviceId`); the platform `hardwareId` string is NOT used
  (it is identical across all units of a model).
- Stations are distilled by `OnHubStations` from four per-client sources, joined on
  IP (and, for telemetry, the obscured-MAC string):
  - `ap-show --network_runtime_state` (field 9) — obscured MAC ↔ IP;
  - `networkState.station_state_updates` (field 16) — connected flag, mDNS name +
    `dns_sd_features`, OUI, wired/wireless, band, guest;
  - `iw dev <iface> station dump` (field 9) — RSSI, tx/rx rate, tx/rx bytes,
    connected time (keyed by obscured MAC);
  - `/proc/net/arp` (field 9) — additional reachable neighbours incl. wired.
  The `Discovered[]` fact subtree deliberately splits **intrinsic device
  attributes** from **the sighting/link** (the observer↔neighbor edge), because a
  −49 dBm reading is a property of the observation, not the device:
  - Intrinsic (→ `proj_discovered` columns; PROMOTED to `Device[]` on reconstruction):
    `ObscuredMAC`, `Oui` (raw 6-hex — NOT a vendor name), `Hostname`, `FriendlyName`
    (mDNS `fn=`), `DeviceType` (mDNS cast-name, e.g. "Nest-Audio" → `Device[].Kind`),
    `Model` (mDNS `model=`/`md=` → `Hardware.SystemModel`).
  - `Discovered[].Service[].Name` — advertised mDNS service types as a real list
    dimension (→ `proj_discovered_services`), not a comma-joined string.
  - `Discovered[].Link.*` — the sighting: `Medium` (wired/wireless), `Band`, `Guest`,
    `SignalDbm`, `Tx/RxRateMbps`, `Rx/TxBytes`, `ConnectedSeconds`. Stays on the
    observation; never promoted. Rendered on the device page's **"Seen By"** tab.
- The AP device also gets Tier-3 facts: `Hardware.TotalMemBytes` (`/proc/meminfo`),
  and from `/etc/lsb-release` `OS.Build`, `OS.Distro` ("Chrome OS") + `OS.Family`
  ("linux"); `OS.Version`/`Hardware.SystemModel`/uptime + `OS.BootTime` (now − uptime)
  from status.
- The AP's own interface inventory is parsed from `/bin/ip -s -d addr` by
  `OnHubApInterfaces` → `Interface[].Name`/`MTU`/`Up`/`IPv4`/`IPv6`/`Type`
  (loopback/bridge), replacing the two synthetic interfaces (a fallback still
  synthesizes br-lan/wan0 if that command is absent). It also merges `ethtool <iface>`
  (→ `SpeedBps`/`Duplex`) and bare `iw dev` (wireless interfaces → `Type=wireless`).
  Interface MACs are obscured, so the collector emits `Interface[].ObscuredMAC` (kept
  raw, never a fingerprint) and `DiscoveryMaterializer.MaterializeInterfaceMacsAsync`
  reconstructs the real MAC into `mac_address` by IP + OUI — the same
  IP-join/OUI-corroboration as the station path, but it fills only the interface (it
  does not resolve/merge a device, since the NIC belongs to the already-identified
  AP). Only interfaces with an IPv4 can resolve (realistically the gateway `br-lan`,
  via another agent's ARP).
- Storage comes from `/bin/findmnt` via `OnHubApStorage`: device-backed mounts →
  `Filesystem[].FsType` (pseudo mounts dropped; no sizes — findmnt carries none), and
  the distinct parent block devices → `Disk[].Name`.
  (Wi-Fi radio channels, speed-test, and mesh topology are parseable but not yet wired.)
- **Every MAC is obscured** — the firmware preserves the real **OUI** (first 3
  bytes) but **obfuscates the device-specific bytes** and appends `*` (verified
  against live ARP: reported `00e0bf1fc40*` ↔ real `00:e0:bf:40:00:73` — only
  `00e0bf` is real). It is NOT a last-nibble mask, so the device portion is unusable
  for matching. The collector emits it as `Discovered[].ObscuredMAC` — a raw kept
  fact, **never** `Discovered[].MAC` — so an obscured value is never a fingerprint
  and no phantom `*` devices are minted.

Server-side reconstruction + promotion (`DiscoveryMaterializer.MaterializeObscuredMacsAsync`,
using `ObscuredMac`): the station's **IP** is the join key. `GetKnownMacsForIpAsync`
returns the real full MACs the server already knows for that IP (ARP, both DHCP-lease
projections, prior non-obscured discovery); `ObscuredMac.Pick` keeps only a candidate
whose **OUI matches** the obscured OUI (guarding against a stale ARP entry / reassigned
IP) and accepts it only when unique. The match is written back as the row's `mac`, the
device is resolved, and the intrinsic attributes are **promoted** onto the resolved
`Device[]` (hostname → `OS.Hostname`, model → `Hardware.SystemModel`, device-type →
`Kind`). Promotion runs every cycle (not only at the reconstruction instant, via
`GetObscuredMacRowsAsync` over all obscured rows), so late-arriving enrichment still
graduates; the COALESCE upserts make re-promotion a no-op. Per-row work is wrapped so a
**locally-administered / randomized MAC** (common on phones/tablets — rejected as a
fingerprint) doesn't abort promotion of the other rows. No unique OUI-corroborated IP
match → only the observation is kept; no device. A Google-Wifi-only station therefore
becomes a device only once some other collector (ARP/DHCP) has also seen its real MAC.

**mDNS identity is anchored to the stable Cast id, not the IP** (`FingerprintType.CastId`).
The `_googlecast` advertisement carries a stable per-device hex id (`Discovered[].CastId`,
e.g. `Google-Nest-Mini-<32hex>` → `<32hex>`) that survives DHCP address changes. In the
promotion pass, `MaterializeObscuredMacsAsync` counts DISTINCT reconstructed MACs per cast
id (computed **in-memory after** this cycle's reconstructions — a DB count taken up front
is stale on the first cycle and would wrongly co-register), then resolves each row:
- **cast id → exactly one MAC** (normal single-device case): co-register
  `[CastId, Mac]` — one clean device merged across the cast id and its hardware MAC.
- **cast id → ≥2 MACs** (a stale mDNS advertisement lingering on an IP the DHCP lease has
  since handed to *other* hardware): resolve by **cast id alone**, so the cast device's
  name never binds to the wrong MAC. The other hardware still materializes as its own
  device via the ARP path. This fixes stale-mDNS-on-reused-IP name smearing (a Nest speaker
  that moved IPs was mislabeling a Ring device that took its old address). No vendor guard —
  the cast id is the reliable anchor.
- **no cast id**: MAC-only, as before.

This integration is unofficial and may break if Google changes the local API.

## Facts and projections

Fact IDs are paths: `Device[id].Interface[eth0].Speed`. Bracketed segments are
**list dimensions**; bare segments group or name the attribute. The projection
router matches facts to `proj_*` tables on `(DimKey, Attribute)`:

- `DimKey` = **all** list segment names in order (`Service|Zone`) — dimensions may
  be separated by bare grouping segments (`Service[x].DNS.Zone[y].Type`).
- `Attribute` = the bare tail after the **last** list segment (`Type`).
- Projection dimension → table column naming is `ToLowerInvariant`, no separator
  (`HoldingRegister` → `holdingregister`). A projection whose dims don't match its
  fact paths fails **silently** — facts land in `facts_history` but the `proj_*`
  table stays empty. `FactTests.Create_DerivesDimKeyAndAttribute` pins the grammar.

**Every fact gets a typed path with a curated home — there is no raw `Attr[]` sink.**
If you parse a signal, you know what it is, so give it a real `FactPaths` constant and
route it: a **projection column** if it's ever queried across devices, otherwise a
**fact view** (`FactViewLibrary`) for device/service detail; genuinely multi-valued
signals become a **list sub-dimension** projection (like `proj_discovered_services`),
not a comma-joined string. Never emit `...Discovered[ip].Attr[key]`-style catch-all
facts — they route nowhere and are invisible to every report. `FactPathRoutingFitnessTests`
enforces this: a new `FactPaths` constant with no projection column and no fact view
**fails the build**. If a parsed value isn't worth showing, don't emit it (and delete
the constant) — don't dump it in a generic bag.

**Monotonic counters route to `metrics_raw`, not `facts_history`.** A path listed in
`FactPaths.MetricPaths` (interface Rx/Tx bytes/packets, discovered-link Rx/Tx bytes —
values that differ on nearly every poll by construction) is written unconditionally to
a separate range-partitioned `metrics_raw` table via `MetricsRepository`, skipping
`facts_history`'s dedup-on-write lookup (pure write amplification for a value that
never repeats). `MetricPartitionService` provisions future day partitions and drops
expired ones. Any query that reads "current value of a fact" across both possibilities
(e.g. `GetDeviceAllFacts.sql`) must union in `metrics_raw` for these paths — see its
`own_metrics`/`sighting_metrics` CTEs.

**"Guess" facts are a separate namespace from canonical facts, not another writer of
the same path.** `Derived.DeviceVendorGuess`/`Derived.DeviceOsGuess` hold best-effort
inference from a proxy signal (SNMP sysDescr, hostname/model prefix, OS distro —
`docs/plans/vendor-derivation-updates.md`'s pattern before it was deleted post-ship),
kept separate from `Derived.DeviceVendorCanonical`/`Device[].OS.Distro` rather than
appended as another source for those paths: a guess re-derived every cycle (fresh
`collected_at` each time) could otherwise silently outrace and clobber an
already-authoritative device-reported value through plain last-write-wins projection.
Reporting only ever consults a `*Guess` path when its canonical counterpart is empty.

**Device vs service identity.** Devices are identified by hardware fingerprints
(`DeviceRegistry`); services (Technitium DNS, Home Assistant) are their own
top-level entity identified by what they *manage* (`ServiceRegistry`: `services` +
`service_fingerprints`, e.g. primary DNS zones) so identity survives host
migration. Agent service batches carry a `ServiceProbe` on the `FactBatchElement`
(no device fingerprints, no hashing); the server resolves/mints a GUID ServiceId
and rewrites fact roots to `Service[{serviceId}]`. A service is *linked* to a
device (`Service[].DeviceId`) only when the host is known — currently loopback
targets only, where the agent uses its own server-assigned DeviceId.

**Promoting other devices out of a service's data.** Some service collectors
report on devices *other* than the service's own host (Home Assistant's device
registry). Whether that promotion belongs in `DiscoveryMaterializer` or inline in
`FactsEndpoint` depends on how the entity's fingerprints get assembled, not on
"is this a service" alone:

- **Multi-source, cross-cycle merge → `DiscoveryMaterializer`.** ARP/DHCP/scanner
  discovery pieces one device's fingerprint set together from facts written by
  *different* collectors in *different* requests (an ARP MAC from one sweep, an
  mDNS hostname from another, arriving at different times). No single in-memory
  batch ever holds the full picture, so a persisted `proj_*` table + a deferred
  reread pass is the only way to reconstruct it. This is what
  `DiscoveryMaterializer`/`DiscoverySource` is for.
- **Single-source, single-cycle → resolve inline, in `FactsEndpoint`, off the
  batch's own `Fact` list.** If one collector reports everything about an entity
  in one cycle (Home Assistant's registry dump), there's no second contributor to
  wait for — writing to a projection and reading it straight back out affords
  nothing a `DeviceRegistry.ResolveWithConnectionAsync` call against the
  already-in-memory facts wouldn't. Do this the same way `FactsEndpoint` already
  resolves device batches inline: group the service batch's rewritten facts by
  the sub-entity's list-dimension key (`fact.ParseId()`), build fingerprints per
  group, resolve on the same open connection, wrap each group's resolve in its
  own try/catch so one bad entity can't fail another or the request. See
  `docs/plans/ha-inline-discovery.md` for the worked example (Home Assistant).

## Incidents and change events

The Dashboard's Needs Attention panel, Recent Activity feed, and each Device Detail's History tab
are all read off two curated tables instead of a raw diff of `facts_history` ("From Noise to
Signal" design proposal): `incidents` (has a lifecycle — opens, later resolves; at most one OPEN
row per `(entity_kind, entity_id, incident_type)`, enforced by a partial unique index, not app
code) and `change_events` (one-shot, timestamped, no duration).

- **Value-driven incident types** (`smart_failing`, `filesystem_full`, `container_not_running`,
  `hardware_failed`) are declared once in `IncidentTypeRegistry.CreateAll()` — a FactPaths
  constant, an open predicate, a resolve predicate, and a reopen window — and evaluated by
  `IncidentEvaluator`, hooked into `FactIngestPipeline.IngestAsync` alongside
  `AppendAsync`/`RouteAsync` (O(1) lookup by `(DimKey, Attribute)`, same shape as
  `ProjectionRouter`). Add a new incident type = one registry entry; the Dashboard/Recent
  Activity/Device History pick it up automatically via `IncidentQueries.GetOpenIncidentCountsAsync`
  — no new SQL there. Open/resolve predicates are independent, not each other's negation, so a
  value can sit in a dead zone that does neither (`filesystem_full`'s 85–90% hysteresis band).
- **Flap suppression (reopen window):** a recurrence within the window continues the same
  incident (`opened_at` unchanged, `resolved_at` nulls back out) instead of minting a new row —
  see `OpenOrTouchIncident.sql`. 5 minutes by default (`IncidentTypeRegistry.DefaultReopenWindow`),
  15 minutes for flap-prone availability incidents (`AvailabilityReopenWindow`).
- **Silence-driven incidents have no fact to evaluate against**, so they're periodic sweeps
  instead of `IncidentEvaluator` hooks, same `BackgroundService` shape as `ReleaseRescanService`:
  `AgentLivenessSweepService` (reuses `agent_liveness()`, the same liveness def as the Fleet
  Dashboard's Agent Health panel) and `FingerprintConflictSweepService` (conflicts are an emergent
  property of `device_fingerprints`, not something one call site "creates" — `ConflictsApi`'s
  merge/exclude actions also resolve immediately rather than waiting for the next sweep tick).
- **Display metadata (label/severity/href) is separate from the open/resolve registry** — see
  `Pages/Reports/IncidentDisplay.cs`. A new incident/event type needs an entry there to show up
  with real text on the Dashboard/Recent Activity/Device History; without one it silently falls
  back to the raw type string (Dashboard) or is simply not looked up (Recent Activity/History
  still render, just with the raw type name as the label).
- **Deliberately deferred, not silently dropped:** `service_down` (no single unambiguous "service
  health" fact path exists yet across DNS/HA/CA service types), `cert_expiring` (a
  clock-driven threshold, not a fact-value change — still sourced from
  `DashboardQueries.GetCertsExpiringAsync`, the one Needs-Attention signal not yet migrated),
  `device_offline` (unlike agents, a device has no per-entity heartbeat/interval to derive a
  threshold from — an open sequencing question in the source design doc itself), and the
  `promoted`/`cert_renewed`/`ip_re-leased`/`os_changed` change-event types (`discovered` and
  `merged` are wired, from `DeviceRegistry.ResolveWithConnectionAsync`'s `IsNew` branch and
  `ConflictsApi.MergeConflictAsync` respectively).

## Database queries

No ORM. SQL is written by hand in `.sql` files and bound to typed C# methods by the
**`[DatabaseCommand]` source generator** (`lib/ITPIE.Database.Generators`).

Queries are grouped **by domain**, one directory per domain directly under `Data/`,
each holding a static `*Queries` class + its `.sql` files:

| Directory | Class | Covers |
|---|---|---|
| `Data/Auth` | `AuthQueries` | sessions, users, bootstrap |
| `Data/Agents` | `AgentQueries` | registration, heartbeat, admin, config, cycles |
| `Data/Credentials` | `CredentialQueries` | credential CRUD |
| `Data/Targets` | `TargetQueries` | unified device + service collection targets |
| `Data/Discovery` | `DiscoveryQueries` | materializer: new-MAC/serial lookups + upserts |
| `Data/Devices` | `DeviceQueries` | device reporting + detail tabs |
| `Data/Reporting` | `ReportingQueries` | fleet-wide lists (hosts, ports, containers, storage, changes) |
| `Data/Reporting` | `DashboardQueries` | Fleet Dashboard aggregates (network/activity/posture/agent-health/collection summaries, composition, trends) |
| `Data/Services` | `ServiceQueries` | service reporting |
| `Data/Maintenance` | `MaintenanceQueries` | OUI, retention, audit |
| `Data/Incidents` | `IncidentQueries` | incidents/change_events — see "Incidents and change events" |

Shared single-column result structs live in `Data/QueryResults.cs`.

**The generator resolves each method's `.sql` file relative to the directory of the
`.cs` file that declares it, and names its generated output per class.** So:

- A query method's `.sql` file MUST sit in the same directory as its `*Queries` class.
- Each domain must be its own uniquely-named class — you cannot split one `[DatabaseCommand]`
  class across multiple files (the per-class output names would collide and implementations
  would silently drop). Add a new domain = new dir + new `*Queries` class.

Query methods are extension methods on `NpgsqlConnection` in namespace
`JMW.Discovery.Server.Queries`; call them as `conn.SomeQueryAsync(...)`.

### Adding a query

1. Add `Foo.sql` in the appropriate `Data/<Domain>/` directory.
2. Declare `public static partial IAsyncEnumerable<...> FooAsync(this NpgsqlConnection connection, ..., CancellationToken cancellationToken);` in that domain's `*Queries` class, marked `[DatabaseCommand]`.
3. Build — the generator emits the implementation and validates it against the live schema in the integration test suite.

## Database access rules

- Inject and use `NpgsqlDataSource`; never `new NpgsqlConnection` directly.
- Plain `await` — no `ConfigureAwait(false)` (ASP.NET Core has no sync context).
- Never use the null-forgiving `!`; handle nulls explicitly.
- Materialize a reader (`ToList`) before issuing per-row queries — no nested open readers on one connection.
- Ingest is the trust boundary: sanitize agent-supplied fact data before writing
  (NUL bytes are stripped; duplicate fingerprints are de-duped) — Postgres rejects
  NUL in text (`22021`) and duplicate `ON CONFLICT` keys in one statement (`21000`).

## Topology graphs (Subnets page)

`/subnets` has three tabs — Topology (L3), Physical/L2, Subnet List — the first two
backed by two independent graph APIs sharing one D3 renderer:

- **L3** (`SubnetsApi.GetGraphAsync`, existing): subnet/router graph synthesized
  query-time from `proj_interfaces`/`proj_dhcp_scopes`/`proj_device_routes` — no
  dedicated `proj_subnets` table.
- **L2** (`L2TopologyApi.GetGraphAsync`, `GET /report/l2-topology`): device/port
  adjacency graph built from `Device[].Neighbor[]` facts (LLDP-derived — see
  `SnmpCollector`/`OnHubApBridgeVlan`/`SshCollector`). This subtree has **no
  projection table** (view-only, `FactViewLibrary` "Neighbors") — read directly
  from `facts_history` via `ReportingQueries.ListNeighborFactsAsync`
  (`Data/Reporting/ListNeighborFacts.sql`, conditional-aggregation pivot since
  Postgres has no built-in crosstab). Each neighbor is resolved to a known
  device by IP first, then MAC (`ResolveIpDeviceAsync`/`ResolveMacDeviceAsync`);
  unresolved neighbors become "unknown" placeholder nodes rather than being
  dropped. Reciprocal LLDP reports of the same physical link (both switches
  usually report each other) are deduped to one edge.

Both graphs serialize to JSON (camelCase, via a page-local `JsonSerializerOptions`
— see `_FilterBar.cshtml`/`Subnets.cshtml.cs` for the established pattern) into a
`data-graph` attribute Razor HTML-encodes automatically; the shared client-side
renderer (`wwwroot/js/topology-graph.js`) reads it with `JSON.parse` and draws a
force-directed layout (pan/zoom/drag/click-to-highlight).

**No build pipeline exists in this repo** (no `package.json`, webpack, vite). Any
JS dependency (D3, previously Mermaid) is a single hand-vendored minified file
under `wwwroot/js/`, referenced with a plain `<script src="..." asp-append-version
="true">` tag — no CDN, no npm. The CSP (`Program.cs`) is `script-src 'self'
'unsafe-inline'` — `'unsafe-inline'` covers inline `<script>` blocks (the D3 init
call, same as Mermaid's old `mermaid.initialize(...)`), but there is **no
`'unsafe-eval'`**; a vendored library that relies on `eval`/`new Function()`
(some D3 plugins do — core D3 doesn't) will silently fail under this CSP.

## Deploy targets

**Production** is `core.home` (`walljm@192.168.1.54`, x86_64 Linux, Ubuntu 24.04), managed
from the separate `core-services` repo (Kanidm, Caddy, Step CA, Postgres, etc.). The server
runs as a **native systemd unit**, not a container:

- Unit `jmw-server.service` (from `deploy/systemd/jmw-server.service`), binary at
  `/opt/jmw/bin/JMW.Discovery.Server` (self-contained single-file `linux-x64` publish),
  config at `/etc/jmw/server.env` (see `deploy/systemd/server.env.example`).
- `WorkingDirectory=/var/lib/jmw`, so static content root is `/var/lib/jmw/wwwroot` —
  deploy it alongside the binary, not just next to it.
- Listens HTTPS directly on `127.0.0.1:8443` via Kestrel
  (`Kestrel__Certificates__Default__Path`/`KeyPath` in `server.env`, a Step CA-issued cert).
  Caddy fronts it at `https://agents.core.home`, verifying via the Step CA chain.
- Database is a dedicated `jmw-postgres` container (loopback `127.0.0.1:5432`) in the
  `core-services` repo, not a container on this host.

**Release automation** (`.github/workflows/release.yml`, triggered on a `v[0-9]+.[0-9]+.[0-9]+`
tag push) runs on a self-hosted GitHub Actions runner **on core.home itself**, so its deploy
steps are local commands, not SSH. It `dotnet publish`es the server + agent (all platforms),
signs agent binaries with `src/Tools/UpdateSign`, then invokes two root-owned helper scripts
via a narrowly-scoped sudoers rule (`/etc/sudoers.d/gha-runner` — the runner can run
*only* these two scripts as root, nothing else):

- `/opt/jmw/bin/gha-deploy-server <server-binary> <wwwroot-dir>` — installs the binary +
  wwwroot, restarts `jmw-server`.
- `/opt/jmw/bin/gha-publish-agent-release <version> <dist-dir>` — copies signed agent
  binaries into `$JMW_RELEASES_DIR` for the self-update mechanism (see "Agent self-update").

These two scripts live only on the host (not tracked in this repo) — update them there
directly if the expected binary names/paths change.

This dev machine = `walljm-macbook`. Manual redeploy without cutting a release: publish
per the "Agent" section above (or the server via the same pattern), scp to the host, then
`sudo systemctl restart jmw-server` (or the relevant agent unit).
