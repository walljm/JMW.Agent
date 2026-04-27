---
id: REQ-032
title: Listening services / port monitoring
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

Agents enumerate locally-listening TCP/UDP ports (port, protocol, bound address, owning process where determinable) and report them on heartbeat. The server detects changes (new port appeared, port disappeared) and surfaces them as events. Optional alerting on unexpected new listeners.

## Acceptance Criteria

1. Listening ports are reported on every heartbeat.
2. Diffs between consecutive reports are computed server-side; new/removed listeners produce activity-feed events.
3. The detail view shows the current set of listeners.
4. The admin can opt into alerting on 'new listener appeared' for selected hosts.
