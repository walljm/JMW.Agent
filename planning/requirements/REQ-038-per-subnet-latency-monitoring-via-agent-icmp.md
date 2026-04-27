---
id: REQ-038
title: Per-subnet latency monitoring via agent ICMP
priority: should-have
category: functional
status: draft
depends_on: [REQ-022, REQ-034]
trace_to: []
revision_id: 1
---
## Description

Each agent periodically pings devices on its subnet (and a configurable list of off-subnet targets, e.g., the LAN gateway and a public DNS server) and reports round-trip latency to the server. The dashboard shows current and historical latency per device and supports alerting on sustained high latency or sustained packet loss.

## Acceptance Criteria

1. Latency probes run on a configurable cadence (default: every 1 minute) — independent of the discovery cadence.
2. Per-device latency series is stored under the standard tiered retention (REQ-022).
3. The detail view (REQ-015) shows recent latency with sparklines once REQ-021 ships.
4. Alerting rules support thresholds on round-trip latency and packet-loss percentage with a configurable sustain duration (e.g., 'RTT > 500 ms sustained for 5 minutes', 'packet loss >= 50% sustained for 2 minutes'). The sustain duration is required, not optional — instantaneous spikes do not fire.
5. Probes degrade gracefully on platforms where raw ICMP is restricted (e.g., Windows non-admin per REQ-053): fall back to TCP connect probes on a configurable port set, or report 'unsupported' rather than crashing.