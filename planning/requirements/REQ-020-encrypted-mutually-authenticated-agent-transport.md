---
id: REQ-020
title: Encrypted, mutually-authenticated agent transport
priority: must-have
category: security
status: draft
depends_on: []
trace_to: DEC-001
revision_id: 1
---

## Description

All communication between agents and the server is encrypted in transit and mutually authenticated. The implementation may use mTLS, a shared bearer token over TLS with strict server-cert pinning, or a comparable scheme — the architect chooses. The relevant property here is: a third party on the LAN cannot impersonate either side, and traffic cannot be read or modified in transit.

## Acceptance Criteria

1. Agent-to-server traffic is TLS-encrypted on every interaction.
2. The server verifies the agent's identity on every request (no anonymous data submission post-registration).
3. The agent verifies the server's identity (cert pinning or trusted CA) and refuses to talk to an unverified server.
4. Credentials/keys are not logged in plaintext.
5. Replaying a captured request from another network position does not result in a successful state change.
