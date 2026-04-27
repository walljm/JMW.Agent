---
id: REQ-036
title: DNS resolution tracking for known devices
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

The server attempts forward and reverse DNS resolution for IP addresses associated with known devices (agent-backed and discovered) and surfaces resolved hostnames on the dashboard. Resolution results are cached and refreshed on a low-frequency schedule.

## Acceptance Criteria

1. Reverse DNS is resolved for every device IP and cached.
2. The cache is refreshed at least daily and on-demand from the device detail view.
3. Resolution failures are recorded as 'no PTR' rather than blank/unknown.
4. Forward-resolution of a hostname is performed when needed for diagnostics.
