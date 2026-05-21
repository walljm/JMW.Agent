---
id: REQ-030
title: SMART data for physical disks
priority: should-have
category: functional
status: draft
depends_on: REQ-022
trace_to: []
revision_id: 1
---

## Description

Agents collect and report SMART attributes for physical disks where the OS and disk support it. The server highlights pre-failure indicators (reallocated sectors, pending sectors, temperature outside spec, etc.) and can fire an alert when SMART crosses configured thresholds.

## Acceptance Criteria

1. SMART attributes are collected at a low frequency (e.g., once per hour) — not per heartbeat.
2. The detail view shows current SMART health summary and key attributes per disk.
3. SMART-based alerting rules can be configured (e.g., alert if reallocated sector count > 0).
4. Disks/OS combinations that don't support SMART are clearly labeled, not silently ignored.
5. SMART history is retained for at least 1 year so trends are visible.
