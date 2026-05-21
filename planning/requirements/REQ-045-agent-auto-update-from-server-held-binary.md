---
id: REQ-045
title: Agent auto-update from server-held binary
priority: nice-to-have
category: operational
status: draft
depends_on: []
trace_to: []
revision_id: 1
---
## Description

The server holds the latest agent binary for each supported platform. On heartbeat, the agent reports its current version; if the server holds a newer version, the agent downloads the update over the secure transport, verifies its signature, and restarts itself. The mechanism is opt-in per-agent and can be disabled globally. A failed update auto-rolls-back to the prior binary.

## Acceptance Criteria

1. The server can be updated with new agent binaries without restarting the server.
2. Agents detect newer versions on heartbeat and self-update if enabled.
3. The update is verified by cryptographic signature before execution. The signing-key trust root used for verification is embedded in the agent at install time and is not retrieved from the server during update operations — a compromised server cannot push a key update to bypass verification. A failed verification aborts the update and is logged as a security event.
4. The admin can disable auto-update globally or per-agent.
5. The prior binary is retained on disk during an update. If the new binary fails to start (defined as 3 consecutive non-zero exits within 60 seconds of launch), the agent automatically rolls back to the prior binary, restarts under the prior binary, and reports a "rollback" event to the server on the next successful heartbeat. Rollback failures (e.g., prior binary also missing/corrupt) are logged locally and the agent stops, leaving the host in a known-failed state rather than crash-looping silently.