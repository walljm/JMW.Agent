---
id: DEC-006
title: Defer SCR-009, SCR-010, SCR-011, SCR-017, SCR-018 to later iteration
date: 2026-06-04
status: draft
---
## Decision
Defer five reporting screens to a later delivery iteration per owner direction.

## Deferred screens
- SCR-009: Security Posture
- SCR-010: Certificate Inventory
- SCR-011: Patch Status
- SCR-017: Sessions
- SCR-018: Accounts

## Rationale
Owner-directed scope reduction for the first delivery iteration. The underlying data (proj_security, proj_device_certs, proj_updates, proj_sessions, proj_local_users) will be collected and stored; the reporting views are simply not built yet.

## In-scope screens (first iteration)
SCR-001 Login, SCR-002 First-Run Bootstrap, SCR-003 Fleet Dashboard, SCR-004 All Hosts, SCR-006 Device Detail, SCR-007 Service List, SCR-008 Service Detail, SCR-012 Storage Health, SCR-013 Open Ports, SCR-014 Container Fleet, SCR-015 ARP Table, SCR-016 Change Feed, SCR-019 Agent List, SCR-020 Agent Detail, SCR-021 Credentials, SCR-023 Error Page, SCR-024 Not Authorized (SCR-005 Device List superseded by SCR-004 All Hosts)

## Related requirements deferred
- REQ-015 Security Posture View (SCR-009) — must-have, view deferred (data collected)
- REQ-016 Certificate Inventory (SCR-010) — must-have, view deferred (data collected)
- REQ-019 Patch Status View (SCR-011) — must-have, view deferred (data collected)
- REQ-023 Admin Accounts and Active Sessions View (SCR-017, SCR-018) — should-have, view deferred

Note: REQ-017 (Open Ports/SCR-013), REQ-018 (Container Fleet/SCR-014), and REQ-022 (Change Feed/SCR-016) are IN SCOPE this iteration; earlier drafts of this block listed them in error.