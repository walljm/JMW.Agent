---
id: API-004
path: /api/v1/report
method: GET
status: draft
---
## Description

Read-only reporting endpoints (user principals, session-cookie auth; viewer + admin). **Read exclusively from projection tables and `facts_history`** (DEC-005, constraints #11). Back the HTMX-polled report screens. All are list endpoints with keyset pagination.

- `GET /api/v1/report/hosts` — all-hosts view (REQ-037, REQ-010 fleet).
- `GET /api/v1/report/services` — service list (REQ-013).
- `GET /api/v1/report/services/{id}` — service detail (REQ-014).
- `GET /api/v1/report/ports` — open-ports cross-device search (REQ-017).
- `GET /api/v1/report/containers` — container fleet (REQ-018).
- `GET /api/v1/report/storage` — storage health (REQ-020).
- `GET /api/v1/report/arp` — ARP cross-reference (REQ-021).
- `GET /api/v1/report/security` — security posture (REQ-015).
- `GET /api/v1/report/certs` — certificate inventory (REQ-016).
- `GET /api/v1/report/patches` — patch status (REQ-019).
- `GET /api/v1/report/changes` — change feed (REQ-022, reads `facts_history`).

## Request Contract
Common: `?after=<opaque>&limit=50` plus per-report filters:
- hosts: `?q=&os=&vendor=&status=managed|discovered|all&source=`
- services: `?q=&kind=`
- ports: `?port=&proto=tcp|udp&q=`
- containers: `?state=&image=`
- storage: `?health=ok|degraded|failing`
- arp: `?ip=&mac=&device_id=`
- changes: `?since=<iso8601>&attribute_path=&device_id=` (time-bounded; reads `facts_history`).

## Response Contract
`200 { "items": [ {...report-specific projection row...} ], "next_cursor": "opaque|null" }`. Each item maps 1:1 to a projection row (or a bounded join ≤2 tables) so HTMX renders without complex client transformation:
- hosts ← `proj_devices` + `proj_systems` (device, hostname, os, vendor, uptime, last_seen).
- services ← `proj_services` / `proj_device_services`.
- ports ← `proj_ports`.
- containers ← `proj_containers`.
- storage ← `proj_disks` + `proj_filesystems`.
- arp ← `proj_device_arp`.
- security ← `proj_security` (firewall, AV, TPM, SecureBoot).
- certs ← `proj_device_certs` (+ `proj_service_ca*` for CA relationships); filters `?expires_before=&issuer=&device_id=`.
- patches ← `proj_updates` (pending count, security count, reboot required); filter `?reboot_required=`.
- services/{id} detail ← `proj_services` + `proj_device_services` (+ `proj_service_ca*`) for one service.
- changes ← `facts_history` (id, attribute_path, key_values, old/new value, collected_at).

## Pagination
**Keyset** on each report's indexed sort key (e.g. hosts `(hostname, device_id)`; certs `(not_after, device_id)`; changes `(collected_at DESC, id)`). `services/{id}` is a single-resource read, not paginated. Indexes specified in schema-additions.md / existing `proj_*` indexes. No offset/limit (DB guidance).

## Error Responses
Uniform envelope. `401 unauthorized`, `404 service_not_found` (`services/{id}`), `422 invalid_cursor` / `invalid_filter` (e.g. bad `since` timestamp, non-numeric `port`). Read-only: no `403` for viewers (both roles may read).

## Cross-References
REQ-010, REQ-013, REQ-014, REQ-015, REQ-016, REQ-017, REQ-018, REQ-019, REQ-020, REQ-021, REQ-022, REQ-037. COMP-006, COMP-007. DEC-004, DEC-005.
