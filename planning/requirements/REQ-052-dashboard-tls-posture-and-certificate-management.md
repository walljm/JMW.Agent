---
id: REQ-052
title: Dashboard TLS posture and certificate management
priority: must-have
category: security
status: draft
depends_on: [REQ-005, REQ-020]
trace_to: DEC-001
revision_id: 1
---

## Description

The dashboard is served over HTTPS by default. The server generates a self-signed TLS certificate at first boot if no cert is supplied, binds the dashboard to HTTPS only, and refuses plain HTTP unless an explicit opt-out flag is set. Agent transport (REQ-020) and the dashboard share the same certificate by default so that agent cert pinning and dashboard browser trust are aligned and rotated together.

## Rationale

REQ-005 #2 requires Secure cookies for sessions, which is meaningless if the dashboard can be served over plain HTTP. REQ-020 already requires mTLS for the agent channel; reusing that cert for the dashboard makes operations (rotation, pinning, renewal) consistent and prevents a second cert lifecycle from drifting.

## Acceptance Criteria

1. On first boot, if no TLS cert/key is supplied, the server generates a self-signed cert+key with a sane SAN (the configured dashboard hostname plus the host IPs) and stores them in the server data directory.
2. The dashboard binds to HTTPS by default. Plain HTTP is only enabled when the operator sets a config flag (e.g., `--dashboard-allow-http`); enabling that flag prints a prominent warning at startup and on every dashboard page (banner) that sessions are unprotected.
3. The admin can supply their own cert+key via config (file paths). Supplied certs override the auto-generated ones.
4. Agent cert pinning (REQ-020) targets the same cert by default. When the cert rotates, agents must re-pin on next handshake; the rotation procedure is documented in operator docs.
5. The dashboard returns HSTS headers when serving HTTPS in non-development mode.
6. Cert details (fingerprint, expiry, SAN list) are visible in admin settings; an alert is raised when the cert is within 30 days of expiry.
