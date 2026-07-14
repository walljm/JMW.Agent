# JMW Discovery

A home/SMB network discovery and inventory system written in **C# / .NET 10**.

A single ASP.NET Core server hosts the dashboard + API and stores everything in
PostgreSQL. Lightweight agents installed on each host stream host inventory back
over HTTPS as *facts*, and double as network sensors that report ARP sightings and
active protocol probes of every other device on their subnet. Devices that can't
run an agent (IoT, printers, routers) show up automatically as "discovered" entries.

## Features

- **Server + agent** split; the agent publishes as a single self-contained binary per platform.
- **PostgreSQL** storage. Facts are appended to `facts_history`; live state is maintained in per-domain `proj_*` projection tables.
- **Fingerprint-based device identity** — devices are merged across observers by MAC / serial / machine-id, so one physical device is one row regardless of how many agents see it.
- **Distributed discovery** — every agent reports its ARP table and runs active scanners (mDNS, SSDP, SNMP, WS-Discovery, HTTP/TLS/SSH banners, BACnet, Modbus, and more); devices are merged by fingerprint.
- **OUI vendor enrichment**, threshold-free change feed, dashboard, and per-device detail tabs.
- **Typed SQL, no ORM** — hand-written SQL bound to C# via a `[DatabaseCommand]` source generator, validated against the live schema in tests.
- HTTPS with agent API-key auth; cookie sessions + RBAC for the dashboard.

## Build

Requires the .NET 10 SDK.

```sh
dotnet build JMW.Discovery.slnx -c Release
dotnet test  test/Unit/JMW.Discovery.UnitTests.csproj
dotnet test  test/Integration/JMW.Discovery.IntegrationTests.csproj   # requires Postgres
```

## Run (development)

The simplest path is docker-compose, which brings up PostgreSQL and the server:

```sh
docker compose up -d --build
```

- Postgres → host `:5433` (db `jmwfacts`, schema `jmwdiscovery`).
- Server  → host `:8090`.

Then:

1. Open `http://localhost:8090/Bootstrap` and create the admin account (first boot only).
2. Log in at `http://localhost:8090/Login`.
3. Configure and start an agent (see below), then approve it on the Agents admin page.

## Agent

The agent is configured with a JSON file (`dev-agent.json` is a sample):

```json
{
  "server_url": "http://localhost:8090",
  "name": "my-host",
  "zone": "local",
  "interval": 30,
  "targets": []
}
```

Each entry in `targets` is a remote device (SSH/SNMP/BACnet/Modbus/Google Wifi) or
service (Technitium DNS, Home Assistant) to collect from, matched to a collector by
`collector_type`. Targets can also be configured centrally via the Fleet UI's
"Targets" tab and are merged with the file-configured ones on every cycle.

Publish a self-contained Linux binary and run it (as root for full collector coverage):

```sh
dotnet publish src/Agent/JMW.Discovery.Agent.csproj -r linux-x64 -c Release \
  /p:PublishSingleFile=true /p:SelfContained=true -o out/agent-linux-x64

sudo JMW_AGENT_STATE_DIR=/var/lib/jmw-agent ./out/agent-linux-x64/JMW.Discovery.Agent agent.json
```

The agent registers, waits to be approved in the dashboard, then streams facts each
cycle. Only changed facts are sent (delta tracking).

## Architecture

- `src/Core` — shared model (`Fact`, `Fingerprint`, analysis/normalizers).
- `src/Agent` — collectors, network scanners, transport, agent runtime.
- `src/Server.Web` — API, ingest pipeline, projections, device registry, Razor Pages dashboard.

Ingest flow: an agent posts a fact batch → `FactRepository` appends to `facts_history`
and `ProjectionRouter` updates the `proj_*` tables → `DeviceRegistry` resolves/merges the
device by fingerprint. See [AGENTS.md](AGENTS.md) for conventions and the query layout,
and [docs/architecture/index.md](docs/architecture/index.md) for the deeper design.

## Deploy

Reference unit files live in [deploy/](deploy/).
