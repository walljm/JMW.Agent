---
id: REQ-040
title: IoT and printer discovery via mDNS service types
priority: should-have
category: functional
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---

## Description

The agent's mDNS listener captures specific service types relevant to home networks: `_googlecast._tcp` (Google Home / Chromecast), `_ipp._tcp` and `_printer._tcp` (printers), `_airplay._tcp`, `_homekit._tcp`, and a configurable additional list. Captured services and their TXT records are reported to the server and surfaced on the device detail view.

## Acceptance Criteria

1. The default service-type capture list includes the types listed above.
2. The admin can extend the capture list from the dashboard without redeploying agents.
3. Service-type observations contribute to device classification (REQ for auto-classification).
4. mDNS TXT records are captured and stored verbatim alongside the service observation.
