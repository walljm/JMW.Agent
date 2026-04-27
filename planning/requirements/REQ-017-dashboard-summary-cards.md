---
id: REQ-017
title: Dashboard summary cards
priority: must-have
category: functional
status: draft
depends_on: [REQ-011, REQ-012, REQ-013]
trace_to: []
revision_id: 1
---
## Description

The dashboard landing page presents a row of summary cards conveying overall network health at a glance: total devices online / offline / pending, open alerts by severity, total free disk across the fleet, devices reporting in the last hour, and recent discovery activity (new devices seen).

## MVP Scope

In MVP, the discovery-activity card shows '0 new devices' (or is hidden behind the same feature flag as REQ-034) until REQ-034 ships. The other cards are populated entirely from agent-reported data (REQ-011, REQ-012, REQ-013).

## Acceptance Criteria

1. Summary cards are visible without scrolling at a 1366×768 desktop viewport (the minimum desktop target documented in quality-standards.md).
2. Each card is clickable and navigates to a filtered view of the underlying records.
3. Numbers refresh without a full page reload at least every 30 seconds (the live-indicator budget in quality-standards.md).
4. The set of cards is fixed for MVP (no user-customizable layout); a future iteration may make this configurable.
5. The dashboard landing page meets the dashboard primary-view budget (<1s) per quality-standards.md.