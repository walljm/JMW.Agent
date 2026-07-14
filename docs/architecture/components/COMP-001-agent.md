---
id: COMP-001
name: Agent
status: draft
---
## Responsibility

Long-running daemon (`Agent` / `Collector` projects) that collects facts and submits them to the server. Iteration-2 changes (per ADR-001, ADR-003):

- Runs as a persistent service (systemd / container), not a one-shot process.
- **Local collection cycle**: on a fixed interval, runs local collectors and configured remote-target collectors (SSH, SNMP, HTTP, cert scan), normalizes/analyzes facts locally, applies delta tracking, and submits batches.
- **Submits `{fingerprints[], facts[]}` batches** — never a `device_id`. The agent no longer calls `POST /devices/identify` and never caches a server-assigned device ID. The server resolves identity at ingest.
- **Self-registration with admin approval** (ADR, D4): on first start the agent generates its own `agent_id` (random UUID), calls `POST /api/v1/agent/register` with hostname/zone/version, and receives an API key. It collects only after the server reports `status=approved` (polled via heartbeat). No pre-shared token.
- **Delta tracking re-keyed onto collection-source identity** (ADR-001): trackers are keyed by the agent's own stable source key — remote targets by target address/URL, the local host by a persistent local key (machine-id fingerprint persisted to the state dir). The tracker is the agent's private write-reduction cache; it never needed the server's device ID. On failed send, the tracker rolls back so the next cycle retries unsent facts.
- **API-key auth** to the server on every call; `agent_id` in the batch is verified server-side against the authenticated key.

Hosts `Agent.PassiveDiscovery` (COMP-002) in-process.

## Interfaces

- **Outbound HTTP → Server.Web (COMP-007) / Server ingest endpoints**: `POST /api/v1/agent/register`, `POST /api/v1/agent/heartbeat`, `POST /api/v1/agent/facts`. gzip-compressed JSON bodies. Bearer API key.
- **Inbound config**: server-side agent config pulled on heartbeat (per DEC-001) — `Interval`, `MaxConcurrency`, `Targets[]`, `Services[]`, per-collector enable/frequency overrides. File-based `AgentConfig` remains valid as a fallback (constraints #9, assumption #3).
- **Local collector SPI**: existing `ICollector` contract (`CollectAsync` producing `Fact[]`). Collectors no longer require a resolved device_id to build fact IDs — they emit facts against a placeholder/local key and the agent attaches fingerprints; the server rewrites fact IDs to `Device[{uuid}].Path` (ADR-001).

## Dependencies

- COMP-002 Agent Passive Discovery (in-process)
- COMP-003 Server Ingest (network)
- COMP-008 Server Auth (API-key issuance/verification)

## Integration Test Boundaries

- **Agent → Server ingest (API call)**: submit a `{fingerprints, facts}` batch for an unknown device; assert server creates a device and stores facts under `Device[{uuid}].*`. Resubmit the same facts unchanged; assert delta tracker suppresses the batch (no new history rows).
- **Agent registration → approval (API call)**: register, confirm `pending`, approve via admin API, confirm next heartbeat returns `approved` and collection begins.
- **Delta rollback (API call + fault injection)**: force a 5xx on `POST /facts`; assert tracker state rolls back and the next cycle re-sends the same facts.
- **Source-key stability (local)**: restart the agent; assert the persisted local key is reused so the local host's delta tracker is not reset (avoids a full re-send storm on restart).
