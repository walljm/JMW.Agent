---
id: REQ-033
title: OS update / pending patches status
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

For agent-backed devices, the agent reports whether the OS package manager has pending updates (count, and security-flagged subset if available). The dashboard surfaces hosts with pending updates and can alert on long-outstanding security updates.

## Acceptance Criteria

1. Pending update counts are collected at a low frequency (e.g., once per hour) per supported OS.
2. Supported OS package managers include the dominant ones for Linux distros Boss runs (apt, dnf/yum, pacman) plus best-effort macOS (brew/softwareupdate where feasible).
3. The summary view surfaces 'N hosts have updates pending'.
4. Optional alerting on 'security update outstanding for > N days'.
5. Unsupported OSes report 'unknown' rather than 'zero pending'.
