---
id: REQ-004
title: First-boot admin bootstrap
priority: must-have
category: security
status: draft
depends_on: []
trace_to: []
---

## Description

On first run with an empty database, the server detects the absence of an admin user and redirects all dashboard requests to a one-time setup page. The setup page collects: admin username, admin password, and an optional pre-shared agent registration key. Once the bootstrap form is submitted, the setup page is permanently disabled and normal login is required for subsequent sessions.

## Acceptance Criteria

1. With an empty DB, every HTTP request to the dashboard is redirected to the setup page.
2. The setup page is reachable only when no admin user exists; once one exists, it returns 404 / not-found.
3. After bootstrap, the admin can log in with the credentials they just set.
4. The optional pre-shared key is stored securely and used by the auto-approval flow (REQ for agent registration).
5. Bootstrap action is recorded in the event log.
