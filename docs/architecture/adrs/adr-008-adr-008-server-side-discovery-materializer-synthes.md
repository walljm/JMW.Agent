---
id: adr-008
title: ADR-008: Server-side Discovery Materializer synthesizes discovered devices from ingested facts
status: draft
date: 2026-06-04
---

## Status
Approved

## Context
Agents submit facts about themselves — including ARP tables, DHCP leases, LLDP neighbor tables, mDNS observations, and other data that contains evidence of *other* devices on the network. These facts are ingested and projected normally. A secondary mechanism is needed to turn that evidence into first-class device records.

## Decision
A Discovery Materializer runs as a post-ingest server-side pass. After each ingest batch is written to the projection tables, the Materializer scans well-known projection columns that carry foreign-device fingerprints and creates discovered device records for any fingerprint not already in the device registry.

Sources scanned:
- proj_device_arp.mac — ARP table entries from every managed device
- proj_dhcp_leases.lease (MAC) — DHCP lease MACs from DNS/DHCP service collectors
- LLDP neighbor facts (when collected via SSH from managed switches)
- proj_dns_records.ip — DNS A/AAAA records that may correspond to devices
- mDNS/SSDP/WSD service facts collected as fact batches

For each unique fingerprint found that has no existing device record:
1. Mint a new device_id
2. Create a device record with management_status = discovered
3. Copy relevant facts (IP, MAC, hostname, vendor) as the initial fact set for that device

Auto-merge (ADR-002) applies: if a new fingerprint matches an existing device, merge rather than create.

## Consequences
- Agents require no changes. They submit facts about themselves; the server does the intelligence work of synthesizing discovered devices.
- The Materializer is idempotent — running it again on the same fact set produces no duplicate devices (fingerprint uniqueness enforced by device_fingerprints primary key).
- Discovery is eventually consistent: a device appears after the next ingest cycle that contains evidence of it, not in real time.
- The Materializer must be aware of which projection columns carry MAC/IP/hostname fingerprints — this is a known, bounded set tied to the projection schema.
