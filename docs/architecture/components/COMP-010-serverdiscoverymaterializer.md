---
id: COMP-010
name: Server.DiscoveryMaterializer
status: draft
---

## Responsibility
Post-ingest server-side pass that synthesizes discovered device records from evidence already stored in projection tables.

## Trigger
Runs after each successful ingest batch is committed to projection tables. Can also run on a scheduled sweep (e.g., every 5 minutes) to catch evidence from older batches.

## Sources
Scans these projection columns for foreign-device fingerprints:
- proj_device_arp.mac + .arp (IP) — from ARP tables on managed devices
- proj_dhcp_leases.lease (MAC) + .ip + .hostname — from DHCP service collectors
- LLDP neighbor facts (when present) — MAC + chassis ID from adjacent devices
- proj_dns_records.ip — DNS records pointing to device IPs
- mDNS/SSDP/WSD service facts — device hostname, MAC, model from discovery collectors

## Algorithm
For each unique (fp_type, fp_value) found in source columns:
1. Check device_fingerprints table — if exists, skip (already tracked)
2. If not found: call DeviceRegistry.ResolveOrCreate(fingerprints) — mints new device_id with management_status=discovered
3. Submit a synthetic fact batch for the new device containing the observed IP, MAC, hostname, vendor as facts
4. Auto-merge applies (ADR-002): overlapping fingerprints from different sources collapse to one device

## Idempotency
Fingerprint uniqueness (PRIMARY KEY on device_fingerprints) prevents duplicate device creation. Safe to run multiple times.

## Relationship to other components
- Runs after Server.Ingest commits projections
- Calls Server.DeviceRegistry for identity resolution
- Feeds facts back through Server.Ingest (or directly to projections) for the discovered device
- Discovered devices appear in all reporting views (proj_devices, All Hosts, etc.)
