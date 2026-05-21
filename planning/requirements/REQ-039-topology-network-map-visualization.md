---
id: REQ-039
title: Topology / network map visualization
priority: should-have
category: functional
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---
## Description

The dashboard provides a visual network map showing subnets as containers, agents as labelled nodes inside their subnets, and discovered devices as nodes connected to their observing agent(s) by edges. The visualization is a graph (spatial layout with explicit nodes and edges), not a nested list or table — the intent is to make topology readable at a glance.

## Acceptance Criteria

1. The view renders as a graph: subnets are visually distinct containers (boxes, regions, or grouping rings); agents and devices are nodes with consistent iconography; edges represent observation relationships and are visually rendered as lines/arcs between nodes. A nested HTML list does not satisfy this requirement.
2. Node icons differ by device class (REQ-042) once classification ships; before that, all nodes share a generic icon and are differentiated only by label.
3. The view scales gracefully for the expected device count (25–75 devices) without becoming unreadable: pan/zoom and force-directed or grid layout are acceptable; static-only layouts that overlap nodes are not.
4. The user can filter by tag/group to reduce visual clutter; filtered-out nodes hide along with their incident edges.
5. The view refreshes on demand; near-live updates are not required for MVP.
6. Topology that requires SNMP / switch-level introspection is explicitly out of scope (see Out-of-Scope).