---
id: REQ-005
title: Session-based admin authentication
priority: must-have
category: security
status: draft
depends_on: []
trace_to: DEC-002
revision_id: 1
---

## Description

The dashboard uses server-side session authentication backed by secure, httpOnly, SameSite cookies. JWT is explicitly not used for primary authentication (see DEC-002).

## Acceptance Criteria

1. Login accepts admin username + password; on success, issues a session cookie.
2. Session cookies are httpOnly, Secure (when served over TLS), and SameSite-Lax or stricter.
3. Sessions can be revoked server-side (logout, session expiry, manual invalidation).
4. Idle sessions expire after a configurable timeout (sensible default).
5. Login attempts are rate-limited to deter brute-forcing.
6. Login and logout events are recorded in the event log.
