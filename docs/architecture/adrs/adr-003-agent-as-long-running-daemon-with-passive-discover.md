---
id: adr-003
title: Agent as long-running daemon with passive discovery
status: draft
date: 2026-06-04
---
## Context

`scratch/` agent runs an interval collection loop. Iteration-2 requirements add persistent passive discovery — mDNS (REQ-031), SSDP (REQ-039), WS-Discovery (REQ-040), LLMNR/NetBIOS (REQ-034), passive ARP monitoring (REQ-033) — which are inherently event-driven (a device advertises whenever it likes), not interval-driven. Owner decisions D3 and D5 govern.

## Alternatives Considered

1. **Poll-only (sample multicast each cycle).** Rejected: misses transient advertisements between cycles; defeats the purpose of passive discovery (REQ-029, REQ-038).
2. **Separate privileged discovery process/daemon.** Rejected by D5: owner wants a single agent process. Two processes complicate deployment, IPC, and the API-key model.
3. **Single long-running daemon with in-process passive listeners (chosen, D3+D5).** Listeners run as persistent background tasks alongside the interval collection loop; raw-socket collectors degrade gracefully if unprivileged.

## Decision

The agent is a long-running daemon (systemd/container). It runs:
- the **interval collection cycle** (local + remote-target collectors), and
- **`Agent.PassiveDiscovery`** (COMP-002): persistent in-process listeners that emit event-driven `{fingerprints, facts}` mini-batches through the same `POST /api/v1/agent/facts` path and the same delta tracker (source-keyed).

Raw-socket collectors (passive ARP monitor, NBNS/LLMNR listener) probe capability at startup; if raw sockets are unavailable they **fall back to snapshot ARP and warn** (D5), reporting `passive_discovery_mode=degraded` on heartbeat.

## Rationale

Event-driven listeners capture advertisements the interval loop would miss. Keeping them in-process (D5) preserves a single deployment unit, one API key, and one config pull. Graceful degradation means the agent still runs useful in containers/hosts without `CAP_NET_RAW`, and the UI can surface reduced discovery confidence (REQ-038).

## Consequences

- **No conflict with EntityStateCache device-affinity** (critic check #2): event-driven mini-batches still resolve to a `device_id` server-side (ADR-001) and hit a single server instance (ADR-006); the cache stays coherent. Affinity was an 80K-device, multi-instance concern that does not apply here.
- Agent needs structured concurrency: bounded submission queue shared by the cycle and the listeners; backpressure if the server is unreachable (with delta rollback).
- Capability detection and degraded-mode reporting are new agent responsibilities.
- Container deployment must document the privilege requirement for full passive discovery (`--cap-add=NET_RAW` or host networking) — see deployment.md.
