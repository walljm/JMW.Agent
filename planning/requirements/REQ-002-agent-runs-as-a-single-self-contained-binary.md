---
id: REQ-002
title: Agent runs as a single self-contained binary
priority: must-have
category: deployment
status: draft
depends_on: []
trace_to: DEC-006
revision_id: 1
---
## Description

The JMW.Agent agent is delivered as a single executable binary with no runtime dependencies. Configuration consists of (at minimum) the server URL and an optional pre-shared key. The agent runs as a long-lived process suitable for management by systemd, launchd, or the equivalent on each platform. Resource-constrained hosts (e.g., older Raspberry Pis) can disable individual subsystems to reduce footprint.

## Acceptance Criteria

1. ./jmw-agent --server=https://... starts the agent with no separate install or package manager step.
2. No external runtime is required.
3. The agent stores minimal local state (registration ID, server cert pin if applicable) in a configurable data directory.
4. The agent recovers from network outages by retrying with backoff and resumes reporting when connectivity is restored.
5. The agent exposes per-feature configuration flags to disable individual subsystems (network discovery, latency monitoring, SMART collection, Docker enumeration) for resource-constrained hosts. Disabled subsystems are not started at all (no idle resource cost).
6. The agent reports its enabled subsystem set on every heartbeat so the server (and dashboard) can show which capabilities each agent is currently providing.