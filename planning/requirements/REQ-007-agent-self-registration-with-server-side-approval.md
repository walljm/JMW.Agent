---
id: REQ-007
title: Agent self-registration with server-side approval
priority: must-have
category: security
status: draft
depends_on: []
trace_to: DEC-001
revision_id: 1
---

## Description

When a new agent starts and points at the server, it registers itself by submitting an introduction (hostname, OS, fingerprint) over an authenticated transport. The server places the registration in a 'pending' state until the admin approves it (or it is auto-approved via pre-shared key — see separate REQ). Until approved, the agent receives no instructions and its reported data is held in a quarantine state (visible in the pending list, not counted in dashboard summaries).

## Acceptance Criteria

1. An agent pointed at the server with no prior registration appears in a 'Pending Approvals' list in the dashboard.
2. The admin can approve or reject pending agents from the dashboard.
3. Approved agents transition to 'active' and begin reporting normally.
4. Rejected agents are denied further communication; their fingerprint is remembered to prevent silent re-registration.
5. Registration events (request, approval, rejection) are recorded in the event log.
6. The transport between agent and server is encrypted and mutually authenticated (mTLS or equivalent shared-token-over-TLS).
