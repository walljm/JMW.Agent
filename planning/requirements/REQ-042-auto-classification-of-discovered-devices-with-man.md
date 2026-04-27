---
id: REQ-042
title: Auto-classification of discovered devices with manual override
priority: should-have
category: functional
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---
## Description

The server attempts to auto-classify each discovered device into one of: server, desktop, phone, printer, IoT, network-equipment, unknown. Classification uses observed mDNS service types, MAC vendor lookups (OUI database), and observed open ports. The admin can override the classification at any time, and the override sticks across re-classification passes.

## Classification Anchors (positive rules)

The classifier must implement at least these positive-classification rules — they are tested explicitly. Additional heuristics are encouraged but not required.

- Device announcing mDNS service `_ipp._tcp` or `_printer._tcp` → **printer**.
- MAC OUI matching Google/Alphabet AND mDNS service `_googlecast._tcp` → **IoT** (Google Cast device).
- MAC OUI matching Apple AND mDNS service `_airplay._tcp` AND no shell/ssh services → **IoT** (Apple TV / HomePod).
- MAC OUI matching a known phone vendor (Apple, Samsung, Google Pixel, etc.) AND no exposed services → **phone**.
- MAC OUI matching a known network-equipment vendor (Ubiquiti, Cisco, MikroTik, TP-Link, Aruba, etc.) AND open SSH/Telnet/HTTPS-management ports → **network-equipment**.
- mDNS service `_workstation._tcp` AND OS announcement string containing "Linux"/"Darwin"/"Windows" → **desktop** or **server** by open-port heuristic (server if port 22 + service ports; desktop otherwise).
- All others, including any genuinely ambiguous case, fall back to **unknown**.

## Acceptance Criteria

1. Each discovered device receives an auto-classification within 60 seconds of the server having sufficient signals (mDNS report or MAC vendor lookup), or is set to "unknown" if signals are insufficient.
2. The classification is visible on the client list and detail views with a clear indicator of "auto" vs "manual override".
3. The admin can change the classification from the detail view; the manual choice persists, is preferred over future auto-classification attempts, and per REQ-035 #3 always wins over auto-reconciliation.
4. The OUI database used for MAC vendor lookups is shipped with the binary and updatable via release.
5. The classifier produces the correct class for each anchor rule above on a test corpus; an implementation that always returns "unknown" fails the test suite.
6. Genuinely ambiguous classifications fall back to "unknown" rather than guessing wildly.