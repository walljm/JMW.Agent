---
id: FLOW-002
title: Investigate a Device
persona: 
status: draft
---

## Goal
The operator (or viewer) reads everything known about one host to diagnose an anomaly. Medium-frequency investigation loop. Capped at 3 clicks from the dashboard (success criterion 3).

## Persona
Both personas (read-only for the viewer).

## Entry point
All Hosts (SCR-004), a filtered reporting row, or an ARP/port cross-reference row. (SCR-005 Device List was superseded by All Hosts.)

## Decision tree
1. From any list/table, click a device row -> Device Detail (SCR-006).
2. Persistent identity header shows hostname, UUID, vendor, model, type, zone, and management status (managed/discovered).
   - **Managed device** -> identity header shows collecting agent; all populated sections render as tabs.
   - **Discovered device** -> in place of collecting-agent, header shows confidence summary ("Seen by N sources, last X ago") and a Discovery Sources panel; only sections with passive data render (no-data sections hidden, REQ-012/030).
3. Operator clicks the tab for the category of interest (System, Hardware, Interfaces, Disks, Filesystems, Security, Updates, Containers, Ports, Services, Users, Sessions, ARP, Routes, Certificates, Battery).
4. Within a tab, read the data; each section shows its own last-updated timestamp.
   - Need provenance (discovered host, distrust a value) -> toggle "show all sources" to reveal lower-precedence conflicting values with source attribution (REQ-035).
   - Need full routes -> "View all routes" loads the paginated route table on demand (REQ-012/DEC-004).
   - Ports/ARP overflow -> paginate / search within the section.
   - Discovered device worth managing -> Promote action (hands off to FLOW-003).

## Data requirements per step
- Step 2: device identity projection + management status; for discovered, per-source first/last seen + observation counts (REQ-038) and canonical+conflicting identity values (REQ-035).
- Step 3-4: the relevant section's projection rows + collected_at timestamps.

## Error / edge paths
- One section's query fails -> that tab shows an inline error + retry; all other tabs still work (REQ-028).
- Section stale -> per-section staleness flag on its last-updated timestamp.
- Device has minimal data (lone-ARP discovered host) -> only identity + Discovery Sources render; the page does not look broken, it looks sparse-by-design.

## Symmetry / recovery
Read-only inspection; back returns to the originating list with its filter state intact (URL-preserved).

## Screens touched
SCR-004/reporting row -> SCR-006 (-> SCR-022 promotion modal for discovered). (SCR-005 superseded by SCR-004.)
