---
id: REQ-037
title: DHCP lease awareness and rogue device detection
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

The system distinguishes devices on static IPs from those receiving DHCP leases (best-effort, based on observed IP stability and network metadata) and flags devices that appear without an expected DHCP lease pattern as potentially rogue.

## Acceptance Criteria

1. Each known device has a 'static / dynamic / unknown' assignment classification, derivable from observed IP stability over time.
2. A new MAC observed on the network that does not match any prior observation generates a 'new device on network' event in the activity feed.
3. Optional alerting on 'unknown new device appeared'.
4. The admin can mark a device as 'expected/known/trusted' to suppress future rogue alerts for it.
