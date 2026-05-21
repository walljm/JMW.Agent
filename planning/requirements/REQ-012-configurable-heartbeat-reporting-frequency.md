---
id: REQ-012
title: Configurable heartbeat / reporting frequency
priority: must-have
category: performance
status: draft
depends_on: []
trace_to: []
---

## Description

Agents send heartbeats and metric reports to the server on a configurable interval. Default is 5 minutes. The admin can override the default globally and per-agent (e.g., a critical server can heartbeat every 15 seconds while idle desktops heartbeat every 5 minutes).

## Acceptance Criteria

1. Default heartbeat interval is 5 minutes.
2. Admin can set a global default and per-agent override from the dashboard.
3. Override changes are applied on the next heartbeat (no agent restart required).
4. Heartbeat interval has a sensible minimum floor (e.g., 5 seconds) to prevent runaway load.
5. Skipped heartbeats are detectable on the server side (used by offline detection — see separate REQ).
