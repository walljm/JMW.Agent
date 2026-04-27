---
id: REQ-014
title: Client list view with live status indicators
priority: must-have
category: functional
status: draft
depends_on: [REQ-011, REQ-012, REQ-013]
trace_to: []
revision_id: 1
---
## Description

The dashboard's primary view lists all known clients (registered agents and, once discovery ships, approved discovered devices) with at-a-glance status: green (healthy/online), yellow (warning/degraded), red (offline or alerting). Each row shows hostname or display name, last-seen timestamp, IP address(es), device class (server/desktop/phone/printer/IoT/unknown), tags, and observer count for discovered devices.

## MVP Scope

In MVP, the list shows registered agents only — discovered devices are introduced by REQ-034 (should-have) and the discovered-device columns (observer count, MAC vendor) appear only after that requirement ships. The 'device class' column is present in MVP but every row reads 'unknown' until auto-classification (REQ-042, should-have) lands; the column header and width remain stable so the visual layout does not shift when classification ships.

## Acceptance Criteria

1. The list shows every active registered agent at all times. Approved discovered devices appear in the list once REQ-034 ships and are filterable independently from agents.
2. Status colors and last-seen timestamps refresh without a full page reload at least every 30 seconds (the live-indicator budget in quality-standards.md).
3. The list is sortable and filterable by status, class, subnet, and tags. The view loads within the dashboard primary-view budget (<1s) per quality-standards.md.
4. Search by hostname / IP / MAC works across both agent and discovered-device records (records that do not yet exist for MVP simply produce no matches).
5. Clicking a row navigates to that device's detail view (REQ-015).