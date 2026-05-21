---
id: REQ-035
title: Server-side discovered-device deduplication by MAC
priority: should-have
category: functional
status: draft
depends_on: REQ-034
trace_to: []
revision_id: 1
---
## Description

The server collates discovery reports from all observing agents into a single device record per unique MAC address. Conflicting metadata (e.g., different hostnames, different IPs at different times) is reconciled via the precedence rules below, and the conflicting per-observer observations remain browsable.

## Precedence Rules

These rules are authoritative — implementations and tests must match them exactly.

1. **IP address and DHCP lease info:** the most recent observation wins, regardless of which agent reported it.
2. **Hostname:** agent-reported hostname (an agent observing the device's own self-announcement) takes precedence over reverse-DNS lookup, which takes precedence over mDNS hostname.
3. **Manual user override:** any field the admin has manually edited (display name, classification, hostname override) always wins over auto-reconciliation, in perpetuity, until the admin clears the override.
4. **MAC vendor / OUI lookup:** stable; sourced from the bundled OUI database; not subject to per-observation precedence.

## Acceptance Criteria

1. A device observed by multiple agents has exactly one canonical record keyed by MAC.
2. The canonical record presents a 'best-known' value for each field exactly per the Precedence Rules above. The source of each canonical value (which observer, which lookup method) is recorded for diagnostics.
3. The list of observing agents is available on the device record and is updated as new observers report the device.
4. Per-observer raw observations are retrievable through the per-observer view (REQ-041) for diagnosis when canonical metadata looks wrong.
5. MAC randomization (modern phones) is detected where possible by short-lived single-observation MACs and surfaced as 'ephemeral' rather than producing a flood of permanent device records (best-effort; see R-5).