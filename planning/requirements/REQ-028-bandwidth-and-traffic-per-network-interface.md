---
id: REQ-028
title: Bandwidth and traffic per network interface
priority: should-have
category: functional
status: draft
depends_on: REQ-022
trace_to: []
revision_id: 1
---

## Description

Agents report cumulative bytes-in/bytes-out and packets-in/packets-out per network interface on each heartbeat. The server computes deltas to derive per-interval throughput, and presents recent throughput on detail views with sparklines.

## Acceptance Criteria

1. Per-interface counters are reported on every heartbeat.
2. Counter rollovers (32-bit / interface restart) are handled gracefully without producing spurious huge spikes.
3. The detail view shows per-interface throughput in human-readable units (KB/s, MB/s).
4. Historical throughput follows the standard tiered retention (REQ-022).
