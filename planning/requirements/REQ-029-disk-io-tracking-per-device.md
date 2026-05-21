---
id: REQ-029
title: Disk I/O tracking per device
priority: should-have
category: functional
status: draft
depends_on: REQ-022
trace_to: []
revision_id: 1
---

## Description

Agents report disk I/O statistics (reads/sec, writes/sec, bytes-read/sec, bytes-written/sec, queue depth where available) per physical disk. The dashboard shows current and historical I/O on the device detail view.

## Acceptance Criteria

1. Disk I/O is reported per physical disk on every heartbeat where supported.
2. Detail view shows per-disk I/O with sparklines and a full chart on expansion.
3. Counter rollovers are handled gracefully.
4. Where the OS does not expose I/O stats, the agent reports 'unsupported' rather than zero (avoids misleading 'idle' charts).
