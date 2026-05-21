---
id: REQ-049
title: Restore from SQLite backup (CLI + UI)
priority: should-have
category: operational
status: draft
depends_on: [REQ-046, REQ-047]
trace_to: []
revision_id: 1
---
## Description

The server supports restoring its SQLite database from a backup file produced by REQ-046 or REQ-047. Restore is exposed two ways:

1. **CLI subcommand:** `jmw-server restore <path-to-backup>` — the primary, scriptable interface.
2. **Dashboard button:** "Restore from backup" in admin settings, which uploads or references a backup file and invokes the same code path as the CLI.

This requirement exists to verify backups (REQ-046, REQ-047) actually work end-to-end. Without it, JMW.Agent ships with a backup feature that has never been exercised.

## Acceptance Criteria

1. The CLI subcommand `jmw-server restore <path>` is implemented and runs against a stopped server data directory. It validates the backup file is a well-formed JMW.Agent SQLite backup before doing anything destructive.
2. The dashboard "Restore from backup" button is reachable from admin settings and invokes the same restore code path as the CLI (no duplicated implementation). The dashboard requires explicit typed confirmation ("type RESTORE to confirm") because the operation replaces current data.
3. The current database file is preserved as a side-by-side `.pre-restore.sqlite` (timestamped) before restore begins, so the admin can revert manually if the restore was a mistake.
4. After a successful restore the server starts (CLI path) or restarts (UI path) on the restored DB and the admin is required to log in again.
5. Restore actions are recorded in the event log on next start: who initiated, source file, pre-restore backup filename.
6. There is at least one automated end-to-end test that runs a backup (REQ-046 or REQ-047) and then a restore on the same data, confirming the restored DB matches the original — this is the test that verifies REQ-046/047 produce valid backups.