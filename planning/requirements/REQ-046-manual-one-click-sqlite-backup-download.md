---
id: REQ-046
title: Manual one-click SQLite backup download
priority: should-have
category: operational
status: draft
depends_on: []
trace_to: DEC-005
revision_id: 1
---

## Description

From the dashboard the admin can download a consistent snapshot of the SQLite database as a single file. The download uses SQLite's online-backup mechanism so it doesn't disrupt running operations or produce a corrupt snapshot.

## Acceptance Criteria

1. A 'Download backup' control exists in the dashboard's settings/operations area.
2. The download is a complete, restorable SQLite file.
3. The download does not require stopping the server and does not corrupt under concurrent writes.
4. The downloaded file is named with a timestamp for easy archiving.
5. The download action is recorded in the event log.
