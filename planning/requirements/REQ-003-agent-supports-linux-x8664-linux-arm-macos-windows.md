---
id: REQ-003
title: Agent supports Linux x86_64, Linux ARM, and macOS
priority: must-have
category: deployment
status: draft
depends_on: []
trace_to: []
revision_id: 1
---
## Description

The agent binary must build and run on the platforms Boss runs full-time in his home network: Linux x86_64, Linux ARM (Raspberry Pi), and macOS (recent). The server only needs to support Linux x86_64 and Linux ARM. Windows agent support is a separate, lower-priority requirement (see REQ-053). FreeBSD/OpenBSD support is explicitly out of scope (see constraints.md).

## Acceptance Criteria

1. Agent binaries are produced for Linux x86_64, Linux ARM (32-bit and 64-bit variants as appropriate), and macOS (universal or per-arch) as part of every release.
2. Each binary is verified by automated CI to start, register against a test server, and report basic system info on its target OS/arch combination.
3. Platform-specific collectors (e.g., disk/network enumeration, SMART, ICMP) report a structured 'unsupported' status rather than crashing when a metric isn't available on a given OS.
4. Documentation lists the supported OS/arch matrix and explicitly notes Windows as should-have (REQ-053) and BSD as out of scope.