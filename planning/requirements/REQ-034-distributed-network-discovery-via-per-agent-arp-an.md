---
id: REQ-034
title: Distributed network discovery via per-agent ARP and mDNS scanning
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: DEC-001
revision_id: 1
---

## Description

Each active agent scans its local subnet at a configurable cadence (default: every 15 minutes) using ARP and listens for mDNS announcements. Discovered devices (MAC, IP, hostname-if-resolvable, mDNS services, mDNS TXT records) are reported to the server. See DEC-001 for the model rationale.

## Acceptance Criteria

1. Each agent performs both ARP scanning and passive mDNS listening on its primary local subnet.
2. Discovery cadence is configurable globally and per-agent (default 15 minutes; minimum 1 minute).
3. Discovery results include MAC, IP, hostname (when resolvable), mDNS service types observed, and any mDNS TXT records.
4. Discovery activity is bounded so it does not generate disruptive scan noise (no flood ARP, no broadcast storms).
5. Failed scans (e.g., no permission to send raw packets on Windows) are logged and surfaced in the agent's status, not silently swallowed.
6. Each discovery report identifies the observing agent so the server can build the observer relationship.
