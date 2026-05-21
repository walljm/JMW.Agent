---
id: REQ-018
title: Event log / activity feed
priority: must-have
category: functional
status: draft
depends_on: []
trace_to: DEC-005
revision_id: 1
---

## Description

The server records significant events to a persistent event log: agent registered/approved/rejected/expired, agent went offline/online, threshold crossed, alert fired/cleared, notification sent, device tag/group changed, admin login/logout, password reset, configuration changed, server started/stopped, backup completed/failed, etc. The log is browsable from the dashboard with filtering by event type, device, and time range.

## Acceptance Criteria

1. All listed event categories are recorded.
2. Each event has a structured shape: timestamp, type, severity, related device (if any), short message, optional structured details.
3. The log is queryable by type, severity, device, and time range.
4. Events are retained for at least 30 days (longer is acceptable; oldest events may be pruned beyond that).
5. The log is exportable as JSON or CSV.
