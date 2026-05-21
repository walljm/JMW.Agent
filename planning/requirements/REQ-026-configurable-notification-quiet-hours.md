---
id: REQ-026
title: Configurable notification quiet hours
priority: should-have
category: usability
status: draft
depends_on: []
trace_to: []
---

## Description

The admin can configure quiet hours during which non-critical notifications are suppressed (e.g., 22:00–07:00 local time). Critical alerts (configurable per rule) can be exempted from quiet hours.

## Acceptance Criteria

1. The admin can configure a quiet-hours window (start time, end time, timezone) from the dashboard.
2. Each alert rule has a 'critical' flag that determines whether it overrides quiet hours.
3. Notifications suppressed by quiet hours are still recorded in the event log and visible in the dashboard alert list.
4. After quiet hours end, a single rollup notification is sent listing alerts that fired during the quiet window (not a flood of individual notifications).
