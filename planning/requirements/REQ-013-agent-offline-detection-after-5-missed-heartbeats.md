---
id: REQ-013
title: Agent offline detection after 5 missed heartbeats
priority: must-have
category: functional
status: draft
depends_on: REQ-012
trace_to: []
revision_id: 1
---

## Description

An active agent is considered offline when 5 consecutive expected heartbeats are missed (relative to its configured interval). At that point the device transitions to a red 'offline' state in the UI and an alert is fired (subject to alerting and quiet-hours rules).

## Acceptance Criteria

1. The 'missed heartbeats' threshold is 5 by default and is configurable globally.
2. The threshold honors per-agent heartbeat overrides (i.e., absolute time-to-offline scales with the agent's interval).
3. Status transitions are recorded in the event log with timestamps.
4. A device that resumes heartbeating after going offline transitions back to 'online' immediately and a recovery event is recorded.
