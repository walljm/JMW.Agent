---
id: REQ-023
title: Configurable alerting thresholds
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

The admin can define alerting thresholds: device offline (built-in, fires after offline detection threshold is reached), disk usage above N%, memory usage above N% sustained for M minutes, CPU above N% sustained for M minutes, and similar threshold rules. Each rule can be scoped globally, per-tag/group, or per-device.

## Acceptance Criteria

1. The admin can create, edit, disable, and delete threshold rules from the dashboard.
2. Rules apply to the right scope (global, tag/group, single device) without affecting unrelated devices.
3. Threshold rules support a sustain duration to suppress flapping (e.g., 'CPU > 90% for 10 minutes').
4. When a threshold is crossed, an alert is created and notifications are dispatched.
5. When the condition clears, an automatic 'resolved' notification is sent (controllable per channel).
6. All threshold-related events are recorded in the event log.
