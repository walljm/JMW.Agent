---
id: REQ-016
title: Device grouping and tagging
priority: must-have
category: functional
status: draft
depends_on: []
trace_to: []
revision_id: 1
---
## Description

The admin can assign tags and group membership to any device — agent-backed in MVP, agent-backed and discovered once REQ-034 ships. Tags are free-form strings (e.g., 'living-room', 'critical', 'guest-vlan'). Groups are named buckets useful for filtering and bulk operations (e.g., 'Pi cluster', 'IoT', 'Family devices').

## MVP Scope

In MVP only registered agents can be tagged/grouped, since discovered devices do not yet exist. The data model and UI are designed to accept discovered-device tagging cleanly when REQ-034 ships — no schema migration or UI redesign should be needed at that point.

## Acceptance Criteria

1. Each device (agent-backed in MVP; agent-backed or discovered once REQ-034 ships) can carry zero or more tags and belong to zero or more groups.
2. The dashboard supports filtering by tag and by group across all relevant views (client list, discovery views once they exist, alerts).
3. Tags and group names autocomplete from existing values to keep usage consistent.
4. Tag/group changes are recorded in the event log.
5. Bulk tag/group assignment is supported from the client list (multi-select).