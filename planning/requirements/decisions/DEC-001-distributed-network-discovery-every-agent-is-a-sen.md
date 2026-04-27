---
id: DEC-001
title: Distributed network discovery: every agent is a sensor
date: 2026-04-27
status: draft
---

## Context

JMW.Agent must discover and monitor agentless devices (printers, IoT, switches, phones) across multiple VLANs/subnets. A central scanner on the server's subnet cannot see devices on isolated subnets (IoT VLAN, etc.).

## Decision

Every deployed Agent participates as a network sensor for its local subnet. Agents perform ARP scanning and mDNS listening on their own L2 segment and report observations to the server. The server collates and deduplicates discoveries by MAC address; the same device may be reported by multiple observing agents.

## Consequences

- Pro: Visibility into isolated subnets without requiring a dedicated probe or trunked port to the server.
- Pro: Multiple observers cross-validate device presence.
- Pro: No special privileges required on the server side.
- Con: Discovery quality depends on agent placement; subnets without agents are invisible.
- Con: Deduplication and conflict-resolution logic is non-trivial (different observers may see different mDNS metadata, hostnames, etc.).
- Con: Adds CPU/network load on every agent (scoped by gentle scan cadence — see REQ for discovery frequency).
