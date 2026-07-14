---
id: adr-005
title: Single PostgreSQL store for all server data
status: draft
date: 2026-06-04
---
## Context

`scratch/` stores facts, projections, and the device registry in PostgreSQL. Iteration 2 adds server-managed configuration (agents, collection targets), credentials, user accounts/sessions, and audit logs (D9). Question: one store or split (e.g. SQLite for config, Postgres for facts)?

## Alternatives Considered

1. **Split stores (SQLite for config/auth, Postgres for facts).** Rejected by D9: two engines, two backup/restore stories, two connection patterns, and cross-store consistency problems (e.g. a credential referenced by a target, an audit log spanning both). No benefit at this scale.
2. **Postgres for facts + an external secrets manager (Vault) for credentials.** Rejected: adds an operational dependency contradicting self-hosted/minimal-dependency constraints (#4, #7). .NET Data Protection (DEC-002, D10) provides at-rest encryption without an external service.
3. **Single PostgreSQL for everything (chosen, D9).** Facts, projections, device registry, server config, credentials (encrypted `bytea`), users/sessions, audit.

**Strongest argument against:** a single DB couples high-write fact ingest with low-write config/auth, and a fact-volume problem could affect the UI. Accepted at this scale (constraints #9, a few hundred devices): write volume is modest, and logical separation (separate tables, separate connection-pool usage) is sufficient. Postgres comfortably handles both workloads at this size.

## Decision

A single PostgreSQL database holds all server data. Access via `NpgsqlDataSource` (registered/injected, never raw connections — DotNet guidance). Raw SQL, no ORM (constraints #3). Credentials encrypted at rest via .NET Data Protection (`bytea`).

## Rationale

One backup/restore unit, one connection story, transactional consistency across config + facts + audit, and no extra operational dependency — directly serving the self-hosted single-operator deployment (constraints #4). Matches the existing schema investment.

## Consequences

- Operator backs up one database (constraints #10 — DR is operator's responsibility).
- Schema grows by the iter-2 tables (schema-additions.md): `agents`, `targets` (originally `collection_targets`, later merged with `service_targets`), `credentials`, `device_aliases`, `users`, `user_sessions`, `audit_log`, plus columns on `devices`/`device_fingerprints`.
- Single-instance Postgres is a single point of failure for the whole system — acceptable for a non-HA monitoring tool (out-of-scope: SLA/HA). Documented in observability.md (health check) and deployment.md.
