---
id: REQ-009
title: Pending agent registrations expire automatically
priority: must-have
category: security
status: draft
depends_on: REQ-007
trace_to: []
revision_id: 1
---

## Description

Pending agent registrations that are neither approved nor rejected within a configurable window are automatically expired. This prevents the pending list from accumulating stale or accidental entries.

## Acceptance Criteria

1. The expiration window is configurable (default: 7 days).
2. Expired pending registrations are removed from the pending list and recorded in the event log.
3. An agent whose pending registration expired can re-attempt registration normally; it is treated as a fresh request.
4. The admin can disable auto-expiry if desired.
