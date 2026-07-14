---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Deployment Architecture

Self-hosted, single host (constraints #4, REQ-024). Two delivery forms, **both required** (D8): a Docker container (+ compose) and a systemd service. Matches the Go app pattern (`deploy/docker/` + `deploy/systemd/`), placed under `scratch/deploy/`.

## Artifacts

| Artifact | Path | Purpose |
|----------|------|---------|
| Server Dockerfile | `scratch/deploy/Dockerfile.server` | Multi-stage build of `Server.Web`; distroless/runtime-only final |
| Compose | `scratch/deploy/docker-compose.yml` | `server` + `postgres`; volumes for key ring + PG data |
| systemd unit | `scratch/deploy/systemd/jmw-server.service` | Native host install |
| Agent Dockerfile | `scratch/deploy/Dockerfile.agent` | Long-running agent daemon (D3); `NET_RAW` for full passive discovery (D5) |
| Agent systemd unit | `scratch/deploy/systemd/jmw-agent.service` | Native agent install |

## `Dockerfile.server` (spec)

Multi-stage:
1. **build**: `mcr.microsoft.com/dotnet/sdk:10.0` — `dotnet restore` + `dotnet publish -c Release -r linux-x64 --self-contained false /p:PublishTrimmed=false` for `Server.Web`. (Confirm exact SDK image tag/digest at build time — DEP-001 uncertainty flag.)
2. **final**: `mcr.microsoft.com/dotnet/aspnet:10.0` (chiseled/distroless variant where available; `aspnet:10.0-noble-chiseled` or equivalent) — copy published output, run as non-root.

Notes:
- No shell needed at runtime; non-root user.
- `HEALTHCHECK` calls `/readyz` (observability.md) — checks PG + key ring.
- The Go app's `Dockerfile.agent` is Go/distroless-static; the **C# agent** image uses the .NET runtime base (it is not a single static binary). smartctl/raw-socket access mirrors the Go pattern (privileged or explicit caps).

## `docker-compose.yml` (spec)

```yaml
services:
  postgres:
    image: postgres:17        # confirm current supported major at deploy time
    environment:
      POSTGRES_DB: jmw
      POSTGRES_USER: jmw
      POSTGRES_PASSWORD: ${PG_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U jmw"]

  server:
    build: { context: ../.., dockerfile: scratch/deploy/Dockerfile.server }
    depends_on:
      postgres: { condition: service_healthy }
    environment:
      JMW_DB_CONNECTION: "Host=postgres;Database=jmw;Username=jmw;Password=${PG_PASSWORD}"
      JMW_DATAPROTECTION_KEYRING: /var/lib/jmw/keyring
      ASPNETCORE_URLS: "http://+:8080"
    ports: ["8080:8080"]
    volumes:
      - keyring:/var/lib/jmw/keyring        # D8: PERSISTED key ring — NOT ephemeral
      - logs:/var/lib/jmw/logs
    healthcheck:
      test: ["CMD", "/app/healthcheck", "http://localhost:8080/readyz"]

volumes:
  pgdata:
  keyring:    # named volume — survives container recreation (D8 critical)
  logs:
```

**D8 key-ring rule (critical):** the Data Protection key ring lives on the `keyring` named volume mounted at `JMW_DATAPROTECTION_KEYRING`. If this were in-container ephemeral storage, every container recreation would generate a new key ring and **all stored credentials would become undecryptable**. `/readyz` fails if the key ring path is missing, surfacing the misconfiguration immediately.

TLS for the UI (assumption #8): terminate at a reverse proxy (nginx/Caddy) in front of `server`, or configure Kestrel HTTPS. Documented as operator responsibility; compose shows HTTP on an internal port.

## `jmw-server.service` (spec) — match Go pattern

```ini
[Unit]
Description=JMW Agent Facts Server
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=simple
User=jmw
Group=jmw
WorkingDirectory=/var/lib/jmw
Environment=ASPNETCORE_URLS=http://127.0.0.1:8080
Environment=JMW_DATAPROTECTION_KEYRING=/var/lib/jmw/keyring
EnvironmentFile=/etc/jmw/server.env
ExecStart=/opt/jmw/bin/Server.Web
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/jmw
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```
`ReadWritePaths=/var/lib/jmw` keeps the persisted key ring + logs writable while `ProtectSystem=strict` locks the rest (matches the Go unit's hardening).

## Configuration

Config via environment variables (container) and `EnvironmentFile`/`server.env` (systemd). No secrets in images.

| Variable | Purpose |
|----------|---------|
| `JMW_DB_CONNECTION` | Npgsql connection string (single PG, ADR-005) |
| `JMW_DATAPROTECTION_KEYRING` | **Persisted** key-ring directory (D8) |
| `ASPNETCORE_URLS` | Kestrel bind |
| `JMW_SESSION_TIMEOUT` | session idle/absolute expiry (optional) |
| `JMW_RETENTION_DAYS` | facts_history retention window (REQ-025, optional) |
| `JMW_TRUSTED_CA_PATH` | PEM file or directory of `*.pem`/`*.crt`/`*.cer` private-CA certs distributed fleet-wide to agents in the heartbeat config, so validating collectors can authenticate private-CA HTTPS endpoints (e.g. `ha.home`). Read at startup — restart to rotate. Optional; unset = agents use system trust only |

Agent config: server-side (DEC-001) pulled on heartbeat; file-based `AgentConfig` remains a valid fallback (assumption #3). Agent env: `JMW_SERVER_URL`, `JMW_AGENT_STATE_DIR` (holds the generated `agent_id`, API key, persistent local delta-source key — ADR-001).

## Migration / startup

- Schema applied via the idempotent `scratch/Schema.sql` + schema-additions.md on startup (or a one-shot init container/`ExecStartPre`). All changes are additive (`IF NOT EXISTS`), safe to re-run.
- First run prints the bootstrap console token (DEC-003) to stdout/journal for the operator to create the initial admin.

## Single-host failure posture

Single PG + single server instance are SPOFs — accepted (non-HA monitoring tool; out-of-scope SLA/HA; ADR-005/006). `Restart=on-failure` (systemd) / `restart: unless-stopped` (compose) provides crash recovery. DB backup/DR is the operator's responsibility (constraints #10).
