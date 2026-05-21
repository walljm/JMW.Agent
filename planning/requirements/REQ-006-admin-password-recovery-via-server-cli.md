---
id: REQ-006
title: Admin password recovery via server CLI
priority: must-have
category: security
status: draft
depends_on: []
trace_to: DEC-003
revision_id: 1
---

## Description

If the admin password is lost, recovery is performed by running a CLI subcommand on the server host (which proves filesystem access to the server's data directory). No email-based recovery is offered.

## Acceptance Criteria

1. `./jmw-server admin-reset` (or equivalent subcommand) prompts for and sets a new admin password.
2. The reset only works when run on the same host with read/write access to the server data directory.
3. The reset is recorded in the event log on next server start.
4. Documentation clearly describes the recovery procedure.
