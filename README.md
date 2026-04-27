# JMW Agent

A lightweight home network monitoring system written in Go.

A single Go server hosts the dashboard + API; lightweight Go agents installed on each
host stream metrics back over HTTPS, and double as network sensors that report ARP
sightings of every other device on their subnet. Devices that can't run an agent
(IoT, printers, routers) show up automatically as "discovered" entries.

## Features

- Single static binary per side — server and agent (no CGO, no runtime).
- HTTPS by default with auto-generated self-signed certs and SHA-256 cert pinning on agents.
- First-boot setup wizard creates the admin user; PSK is printed once at startup.
- Live system metrics: CPU, memory, load, uptime, per-disk usage, per-interface counters.
- Threshold alerting with email + webhook delivery and a firings/event log.
- Distributed network discovery: every agent reports its ARP table; devices are merged
  by MAC across observers.
- SQLite storage in WAL mode — one file, one process.
- Dark-mode dashboard built with vanilla JS, plain HTML templates, and a small CSS.
- Smoke test that exercises register → approve → heartbeat → metrics → discovery.

## Build

Requires Go 1.26+.

```sh
make build           # builds bin/jmw-server and bin/jmw-agent for the host
make build-all       # cross-builds for linux/darwin × amd64/arm64
make test            # runs go test ./...
make vet             # runs go vet ./...
```

## Run (development, single host)

```sh
# 1. Start the server in one terminal. First boot prints the agent PSK.
./bin/jmw-server -config server.toml

# 2. Visit https://localhost:8443 to create the admin account.
#    (Browser will warn about self-signed cert; proceed once.)

# 3. Configure agent.toml with the printed PSK and start the agent:
cat > agent.toml <<EOF
server_url    = "https://localhost:8443"
psk           = "PASTE_PSK_HERE"
id_file       = "./agent.id"
interval_secs = 30
EOF

./bin/jmw-agent -config agent.toml

# 4. Approve the agent under https://localhost:8443/pending.
```

## Deploy

Reference unit files live in [deploy/](deploy/):

- [deploy/systemd/jmw-server.service](deploy/systemd/jmw-server.service)
- [deploy/systemd/jmw-agent.service](deploy/systemd/jmw-agent.service)
- [deploy/launchd/com.walljm.jmw.server.plist](deploy/launchd/com.walljm.jmw.server.plist)
- [deploy/launchd/com.walljm.jmw.agent.plist](deploy/launchd/com.walljm.jmw.agent.plist)

## Architecture

See [planning/architecture/overview.md](planning/architecture/overview.md) and
[planning/implementation/plan.md](planning/implementation/plan.md).

## Layout

```
cmd/server/        # jmw-server entry
cmd/agent/         # jmw-agent entry
internal/server/
  config/          # TOML config loader, PSK bootstrap
  store/           # SQLite + embedded migrations + repos
  tls/             # self-signed cert bootstrap
  http/            # routes, middleware, handlers, templates, static
  alerting/        # threshold evaluator
  notify/          # email + webhook dispatchers
internal/agent/
  config/          # agent TOML config
  identity/        # persistent agent ID
  collect/         # OS metric collectors (linux + darwin)
  discover/        # ARP scanners
  transport/       # pinned-TLS HTTP client
internal/shared/
  proto/           # wire types (versioned API)
  version/         # build-time version (-ldflags)
internal/smoke/    # end-to-end test
deploy/            # systemd + launchd unit files
```

## Status

MVP per [planning/implementation/plan.md](planning/implementation/plan.md):

- P1 Foundation
- P3 Metrics + alerting
- P4 Network discovery (ARP)
- P5 Deploy units + smoke test

Future work (post-MVP): mDNS service discovery, topology graph, Docker/service
inventory, retention rollups, agent auto-update with ed25519 signing,
SMART/disk-IO, multi-user RBAC.
