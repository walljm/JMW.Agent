---
id: REQ-011
title: Basic system info collection per agent
priority: must-have
category: functional
status: draft
depends_on: []
trace_to: DEC-005
revision_id: 1
---
## Description

Each agent collects and reports basic system information about its host: CPU model and core count, total/available memory, disks (mount points, filesystem, total/free bytes), network interfaces (name, MAC, IPs, link state), OS name and version, kernel version, hostname, and uptime since last boot.

## Acceptance Criteria

1. All listed fields are collected on every supported OS (with graceful degradation where unsupported, per REQ-003 AC #3).
2. Static info (CPU model, OS version) is reported on registration and on agent restart, and refreshed at least once per heartbeat.
3. Volatile info (memory free, disk free, link state) is reported on every heartbeat.
4. Collection completes in under 5% of the configured heartbeat interval, and never exceeds 1 second wall-clock regardless of interval.
5. Field semantics are consistent across OSes (e.g., 'memory free' has the same meaning on Linux and macOS, accounting for buffer/cache appropriately) and the consistency rule is documented per field.