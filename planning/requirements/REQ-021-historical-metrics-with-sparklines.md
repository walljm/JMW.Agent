---
id: REQ-021
title: Historical metrics with sparklines
priority: should-have
category: functional
status: draft
depends_on: REQ-022
trace_to: []
revision_id: 1
---

## Description

Numeric metrics (CPU, memory, disk usage, bandwidth, etc.) are stored as time-series data and visualized as sparklines on detail views and selected summary contexts. The user can expand a sparkline to a full chart with selectable time ranges.

## Acceptance Criteria

1. Every numeric metric on the device detail view shows a sparkline of recent history.
2. Clicking a sparkline expands to a chart with at least these ranges: last 1h, 24h, 7d, 30d, 1y.
3. Charts use downsampled data appropriate to the range to keep response times snappy.
4. Hovering a chart point shows the exact value and timestamp.
