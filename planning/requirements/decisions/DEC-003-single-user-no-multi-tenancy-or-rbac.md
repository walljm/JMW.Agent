---
id: DEC-003
title: Single-user, no multi-tenancy or RBAC
date: 2026-04-27
status: draft
---

## Context

JMW.Agent is a personal home-lab project for a single household with one operator.

## Decision

The system supports exactly one admin user. No additional users, no role-based access control, no organizations or tenants.

## Rationale

- Adding a second user has zero current value and meaningful complexity cost (roles, permissions, invitation flow, audit-by-user).
- The dashboard sits behind LAN-only access controls; the household trust boundary is implicit.
- If the project ever needs multi-user, that's a deliberate future redesign — not premature scaffolding now.

## Consequences

- Auth model is single-credential bootstrap + session.
- Event log records actions but doesn't need a 'user_id' dimension beyond 'admin' and 'system'.
- Password recovery is a server-side CLI reset (no email-reset flow needed for one user).
