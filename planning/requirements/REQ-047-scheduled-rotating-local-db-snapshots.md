---
id: REQ-047
title: Scheduled rotating local DB snapshots
priority: should-have
category: operational
status: draft
depends_on: []
trace_to: DEC-005
revision_id: 1
---

## Description

The server takes scheduled local snapshots of its SQLite database to a configurable directory and rotates them on a configurable retention policy (e.g., daily for 7 days, weekly for 4 weeks).

## Acceptance Criteria

1. A snapshot schedule is configurable from the dashboard (frequency + retention).
2. Snapshots are taken using the SQLite online-backup mechanism.
3. Snapshot files are written to a configurable directory; default is alongside the live DB.
4. Old snapshots are pruned automatically per the retention policy.
5. Snapshot success/failure events are recorded in the event log.
