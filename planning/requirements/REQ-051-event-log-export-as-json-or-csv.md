---
id: REQ-051
title: Event log export as JSON or CSV
priority: should-have
category: data
status: draft
depends_on: REQ-018
trace_to: []
revision_id: 1
---
## Description

The admin can export filtered event-log records as JSON or CSV from the dashboard. Export respects the active filters (event type, severity, device, time range) and applies a hard cap to prevent runaway exports.

## Acceptance Criteria

1. An "Export" control is available on the event log view.
2. Exports respect active filters.
3. JSON exports are well-formed and machine-parsable.
4. CSV exports are RFC 4180-compliant with quoted fields.
5. Large exports stream rather than buffering the entire result in memory.
6. Exports are capped at 10 million rows or a 30-day window, whichever is smaller. When the cap is hit, the export still completes and returns the capped subset, and the UI surfaces a clear warning ("Export truncated: result exceeded the 10M-row / 30-day cap; narrow your filters or use multiple exports") with the row count actually returned.