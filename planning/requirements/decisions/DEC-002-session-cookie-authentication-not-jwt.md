---
id: DEC-002
title: Session cookie authentication, not JWT
date: 2026-04-27
status: draft
---

## Context

JMW.Agent dashboard needs auth despite running on a trusted home network. Choice between server-side sessions (cookie) and stateless tokens (JWT).

## Decision

Use server-side sessions backed by secure, httpOnly, SameSite cookies. Do not use JWTs for primary dashboard authentication.

## Rationale

- Single-user, single-server topology — none of JWT's stateless distribution benefits apply.
- Server-side sessions support immediate revocation (logout-everywhere, password reset).
- Smaller attack surface: no algorithm-confusion risks, no key rotation mechanics, no localStorage exposure to XSS.
- Cookie+CSRF is well-understood and adequate for a LAN-only dashboard.

## Consequences

- Sessions persist in SQLite alongside other state.
- Session table needs periodic cleanup of expired rows.
- Agent-to-server transport is a separate concern (handled by mTLS / shared token, not session cookies).
