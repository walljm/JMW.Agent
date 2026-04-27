---
id: REQ-050
title: Server is resilient to agent and network failures
priority: must-have
category: resilience
status: draft
depends_on: []
trace_to: []
---

## Description

The server tolerates expected operational failures: agents disappearing without warning, agents flooding with retries after a network partition, malformed reports, slow clients, clock skew on agents. The server continues to serve dashboard requests under these conditions.

## Acceptance Criteria

1. An agent that loses network connectivity and reconnects later resumes normally without orphaning data on either side.
2. A flood of reconnect/heartbeat traffic from many agents simultaneously does not bring down the server (rate-limiting, queueing, or backpressure is in place).
3. Malformed reports are rejected with a clear error and recorded in the event log; they do not corrupt state.
4. Agent timestamps with significant clock skew are normalized to server time on ingest.
5. The server continues serving dashboard pages while agent ingestion is under load.
