---
id: REQ-048
title: Optional remote DB sync
priority: nice-to-have
category: operational
status: draft
depends_on: []
trace_to: DEC-004
revision_id: 1
---

## Description

The admin can configure an optional remote sync target for backups (rsync over SSH, scp, or pushing to a configured location). Snapshots are pushed to the remote target after they are taken locally.

## Acceptance Criteria

1. The admin can configure a remote target with credentials/keys from the dashboard.
2. Remote sync is opt-in; default is off.
3. Sync failures are recorded in the event log and surfaced as alerts (configurable).
4. The admin can trigger a manual sync at any time.
5. Credentials for remote sync are stored encrypted at rest.
