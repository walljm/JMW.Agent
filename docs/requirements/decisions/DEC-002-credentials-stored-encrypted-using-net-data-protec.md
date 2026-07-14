---
id: DEC-002
title: Credentials stored encrypted using .NET Data Protection
date: 2026-06-04
status: draft
---

## Decision
Credentials (SSH passwords, SSH private keys, SNMP community strings, API tokens, username/password pairs) are stored in the PostgreSQL database encrypted using ASP.NET Core's Data Protection API (IDataProtectionProvider). Keys are stored on the host filesystem outside the database.

## Rationale
The self-hosted constraint and minimal-dependencies constraint rule out external secret management systems (Vault, AWS Secrets Manager, etc.). The .NET Data Protection API is part of the platform, handles key rotation transparently, and is well-audited. It provides adequate security for a single-operator deployment.

## Alternatives Considered
- **Plaintext storage**: Unacceptable — violates the security constraint.
- **HashiCorp Vault**: External dependency, operational overhead, overkill for a single-operator deployment.
- **OS keychain / DPAPI**: Platform-specific; .NET Data Protection is cross-platform and more suitable for a Linux server deployment.

## Consequences
- The Data Protection key ring location must be configured and persisted across restarts (important in containerized deployments).
- Loss of the key ring means all stored credentials must be re-entered.
- Credentials are write-only in the UI — secret values cannot be retrieved after entry.
