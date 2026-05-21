---
id: REQ-027
title: Per-device uptime and reboot history
priority: should-have
category: functional
status: draft
depends_on: REQ-022
trace_to: []
revision_id: 1
---

## Description

For agent-backed devices, the system tracks reboot events (uptime resets) and presents a history of when the device rebooted, current uptime since last boot, and a count over time periods.

## Acceptance Criteria

1. Each detected uptime reset is recorded with a timestamp.
2. The detail view shows current uptime and a list of recent reboots.
3. A reboot during a period when the agent was offline (and thus could not report) is detected on next reconnect (the agent's reported boot time is used as the reboot timestamp).
4. Reboot history is retained for at least 1 year.
