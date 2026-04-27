---
id: REQ-019
title: Dark-mode-default responsive UI
priority: must-have
category: usability
status: draft
depends_on: []
trace_to: []
---

## Description

The dashboard ships with a dark theme as the default. A light theme is offered as an alternative. The UI is responsive: usable on modern desktop browsers (Chrome, Firefox, Safari latest), tablet, and phone form factors so Boss can check status from any device on the LAN.

## Acceptance Criteria

1. Dark theme is the out-of-the-box default; light theme is available via a toggle.
2. The selected theme persists across sessions (per-browser).
3. All primary views (client list, detail view, summary cards, event log, settings) are usable on a phone-sized viewport without horizontal scrolling.
4. Tablet layouts make effective use of the larger viewport (not just a stretched mobile layout).
5. Legacy IE / pre-ES2017 browsers are not supported.
