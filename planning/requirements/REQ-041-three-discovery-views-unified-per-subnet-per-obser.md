---
id: REQ-041
title: Three discovery views: unified, per-subnet, per-observer
priority: should-have
category: usability
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---

## Description

The discovery section of the dashboard offers three distinct views: (1) a unified, deduplicated list of all known devices across the network; (2) a per-subnet view grouping devices by the subnet they appear on; (3) a per-observer view answering 'what does Agent X currently see?' for diagnostic purposes.

## Acceptance Criteria

1. All three views are reachable from the discovery section's navigation.
2. The unified view uses the canonical deduplicated record per device.
3. The per-subnet view lists subnets and their resident devices, with subnet-level metadata (CIDR, gateway, observing agents).
4. The per-observer view shows, for a selected agent, the raw observations that agent has reported, including timestamps.
5. Filtering, sorting, and tag/group operations work consistently across all three views.
