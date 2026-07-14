---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Observability

Scoped to a single-host, single-operator deployment (constraints #4, #9). Lightweight and self-contained — no external APM dependency (constraints #7).

## Logging

- Structured logging via `Microsoft.Extensions.Logging` (see conventions.md for levels and the per-request fields). Default sink: stdout/stderr (captured by systemd journal or `docker logs`); optional rolling file under `/var/lib/jmw/logs`.
- Correlation id per request (generated if absent), propagated to the agent in responses for cross-correlation.
- **Redaction**: credential plaintext, API keys, session cookies, and passwords are never logged (conventions.md).

## Metrics

Exposed at `GET /metrics` (Prometheus text format) — optional scrape, no push. Minimal set sufficient for a small deployment:

- `ingest_batches_total{result}` — accepted/rejected fact batches.
- `ingest_facts_total` — facts appended to history.
- `device_merges_total` — auto + manual merges (ADR-002).
- `agents_active` — agents with a heartbeat within the liveness window.
- `passive_discovery_mode{agent}` — full/degraded gauge (D5).
- `http_request_duration_seconds{route}` — latency histogram (backs the REQ-026 <1s budget for ≤500 devices).
- `db_query_duration_seconds{op}` — DB timing for the hot report/ingest paths.

(Built on `System.Diagnostics.Metrics`; the Prometheus endpoint is a thin exporter. If the exporter would require a heavy dependency, defer the `/metrics` endpoint and rely on logs — flagged for implementation-time decision.)

## Tracing

No distributed tracing this iteration (single process; out-of-scope complexity). Correlation ids + structured logs cover the single-host need. `System.Diagnostics.Activity` spans may be added later without re-architecture.

## Health Checks

- `GET /healthz` (liveness) — process up; returns `200` always when serving.
- `GET /readyz` (readiness) — checks PostgreSQL connectivity via `NpgsqlDataSource` and Data Protection key-ring availability (so a missing/unmounted key ring fails readiness loudly rather than silently failing credential decryption later — D8). Returns `200` only when both pass; `503` otherwise.
- Used by Docker `HEALTHCHECK` and systemd watchdog (deployment.md).

## Agent-side liveness

- Heartbeat (`POST /api/v1/agent/heartbeat`) updates `agents.last_heartbeat` and reports `passive_discovery_mode`. The fleet dashboard (REQ-010) derives online/offline from the heartbeat window; degraded passive discovery is surfaced per agent (REQ-038).

## UI error states (REQ-028)

- Failed report/partial loads render an inline error state (retry affordance), not a stack trace. 5xx responses carry the uniform error envelope + correlation id so an operator can find the matching log line.
