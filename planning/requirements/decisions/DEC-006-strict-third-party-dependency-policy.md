---
id: DEC-006
title: Strict third-party dependency policy
date: 2026-04-27
status: draft
---

## Context

Go implementation, single-developer maintenance, long expected lifetime.

## Decision

Dependencies must be justified individually. Default to the Go standard library. Non-stdlib dependencies are acceptable only when (a) the in-house implementation cost is significantly higher than the maintenance cost of the dep, or (b) the domain genuinely requires a vetted library (cryptography, complex protocols, SQLite driver, etc.).

## Rationale

- Every dep is a maintenance + supply-chain liability.
- Solo maintainer cannot audit a large dep tree.
- Go's stdlib covers most needs (HTTP, crypto/tls, encoding/json, database/sql, etc.).

## Consequences

- Architect documents dep choices in DEP-NNN records with justification.
- New deps added during implementation require justification and pass through the supply-chain audit step.
- Prefer single-maintainer-risk-free libraries (mature, multiple maintainers, broad adoption).
- Likely accepted areas: SQLite driver, mDNS library (zeroconf/bonjour is non-trivial), maybe a router (or just net/http).
