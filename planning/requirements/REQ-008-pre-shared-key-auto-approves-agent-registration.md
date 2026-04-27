---
id: REQ-008
title: Pre-shared key auto-approves agent registration
priority: must-have
category: security
status: draft
depends_on: [REQ-004, REQ-007]
trace_to: DEC-001
revision_id: 1
---

## Description

If an agent presents a valid pre-shared key (configured at server bootstrap), its registration is auto-approved without manual admin action. This supports rapid onboarding of multiple agents during initial setup or when standing up a new fleet of devices.

## Acceptance Criteria

1. The pre-shared key is configured during first-boot bootstrap (optional — the admin can skip it).
2. The admin can rotate or revoke the pre-shared key at any time from the dashboard.
3. Agents that present the current key are auto-approved on first registration.
4. Agents without the key (or with a stale key) fall through to the manual approval flow.
5. Auto-approval events are clearly tagged in the event log so the admin can audit them.
6. The pre-shared key is stored securely (hashed at rest where feasible).
