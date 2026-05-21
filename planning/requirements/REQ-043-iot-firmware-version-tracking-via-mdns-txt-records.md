---
id: REQ-043
title: IoT firmware version tracking via mDNS TXT records
priority: should-have
category: functional
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---

## Description

When mDNS TXT records contain firmware version or model information (common for Google Home/Chromecast and many IoT devices), the server extracts and tracks these values per device. Changes are recorded in the activity feed.

## Acceptance Criteria

1. A documented set of well-known TXT keys is parsed (e.g., 'rs', 'md', 'fw', 'fn' for cast devices) into structured fields.
2. Firmware version changes between observations are surfaced as activity-feed events.
3. The admin can query the dashboard for 'all devices running firmware X' for vulnerability triage.
