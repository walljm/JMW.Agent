---
id: REQ-015
title: Client / device detail view
priority: must-have
category: functional
status: draft
depends_on: [REQ-011, REQ-012, REQ-013]
trace_to: []
revision_id: 1
---
## Description

Clicking a client opens a detail view showing all known information about that device: identity (hostname, MAC, IPs, OS, observer info if discovered), live system metrics (CPU, memory, disks, network), historical sparklines, recent events, configured alerts/thresholds, and the tags/group memberships. For agent-backed devices the view also includes Docker containers, listening services, and reboot history; for discovered-only devices it shows mDNS services, manufacturer (MAC vendor lookup), and which agents have observed it.

## MVP Scope

MVP shows: identity, live system metrics from REQ-011, recent events, tags/groups, and admin actions. Sections for historical sparklines (REQ-021), Docker containers (REQ-031), listening services (REQ-032), reboot history (REQ-027), SMART (REQ-030), mDNS services (REQ-040), MAC vendor (REQ-035), and discovered-device observer info (REQ-034) appear only after their respective should-have REQs ship. The page layout is stable: sections that depend on unshipped REQs are simply absent rather than empty placeholders.

## Device Lifecycle

The detail view is also where lifecycle transitions happen:

- **Deregister an agent:** moves the agent to 'deregistered' state. Historical metrics for that device are retained per REQ-022's tiered retention policy. The device card disappears from the active client list (REQ-014) but remains searchable and reachable via an 'Archived' filter; its detail view loads read-only with a clear 'deregistered on YYYY-MM-DD' banner.
- **Mark a discovered device as 'ignored':** hides the device from the main client list and from all alerting rules. The record and its historical observations are retained in the database; the action is reversible via an 'Ignored' filter view.
- **Discovered device idle for 30 days:** auto-archived (hidden from main views, retained in the database). The user can manually purge an archived device, which permanently deletes its record and observations.

## Acceptance Criteria

1. The detail view loads within the dashboard primary-view budget (<1s) per quality-standards.md, including with the full expected fleet size of 75 devices in the database.
2. The view differentiates clearly between 'registered agent' and 'discovered-only' devices, showing only sections meaningful for each.
3. Live metrics refresh without full page reload, meeting the live-indicator budget in quality-standards.md (<=30s refresh).
4. The admin can edit display name, tags, group, and device class from the detail view; changes are recorded in the event log.
5. The admin can manually trigger lifecycle and operational actions: refresh discovery, force a heartbeat, deregister an agent, mark a discovered device as ignored, restore an ignored device, archive/purge a discovered device. Each action is logged to the event feed.
6. Deregistered and ignored devices remain reachable via 'Archived' / 'Ignored' filters; the active list (REQ-014) excludes them by default.
7. Discovered devices not observed for 30 days are auto-archived; the threshold is configurable but defaults to 30 days.