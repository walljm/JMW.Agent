---
id: REQ-022
title: Tiered metric retention: 7d raw / 30d 5-min / 1y hourly
priority: should-have
category: data
status: draft
depends_on: []
trace_to: DEC-005
revision_id: 1
---

## Description

Time-series metrics are retained at three resolutions:
- Raw samples (every reporting interval) for 7 days.
- 5-minute downsampled aggregates for 30 days.
- Hourly downsampled aggregates for 1 year.

Downsampling runs on a schedule. Older data is pruned automatically.

## Acceptance Criteria

1. After 7 days of operation, the raw sample table contains roughly 7 days of data per metric per device.
2. The 5-minute aggregate table covers approximately 30 days; the hourly table approximately 1 year.
3. Downsampling correctness: 5-minute and hourly aggregates carry min/max/avg (or equivalent statistics) so charts at lower resolutions remain meaningful.
4. Pruning is observable in event log entries when retention windows trim data.
5. Retention windows are configurable (defaults stated above).
