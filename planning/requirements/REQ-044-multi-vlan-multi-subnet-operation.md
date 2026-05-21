---
id: REQ-044
title: Multi-VLAN / multi-subnet operation
priority: must-have
category: deployment
status: draft
depends_on: []
trace_to: DEC-001
revision_id: 1
---

## Description

The system operates correctly when agents are placed on isolated subnets (different VLANs) from the server. The only required network path is from each agent to the server's HTTPS endpoint; agents do not need to reach each other or other subnets.

## Acceptance Criteria

1. An agent on an isolated subnet (e.g., IoT VLAN with strict egress rules to the server only) functions normally.
2. Discovery operates only on the agent's local subnet — agents do not attempt to scan other subnets.
3. Documentation describes the firewall rules required (egress from each subnet to the server's port).
4. Subnet-level visibility in the dashboard is determined by which subnets have at least one agent on them; subnets without an agent are explicitly invisible (not silently merged into a neighbor).
