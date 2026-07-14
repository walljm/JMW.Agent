---
id: DEP-004
package: ASP.NET Core Data Protection
version: net10.0 (SDK-bundled)
license: MIT
adr_ref: adr-005
status: draft
---
## Justification

At-rest encryption for stored credentials (REQ-007, DEC-002, D10) and protection of session/anti-forgery payloads. Chosen over an external secrets manager (rejected in ADR-005) to avoid an operational dependency on a self-hosted single-operator deployment (constraints #4, #7). First-party part of `Microsoft.AspNetCore.App` shared framework — not a third-party dependency; recorded for completeness because it is a security-critical, cryptographic component.

The key ring **must be persisted to a volume path** (D8) so the server can decrypt credentials after restart/redeploy; an ephemeral in-container key ring would silently lose the ability to decrypt stored secrets.

## Health Assessment
- Maintenance activity: Microsoft-maintained, part of the .NET SDK; security-reviewed cryptographic stack.
- Community adoption: standard ASP.NET Core mechanism for data protection.
- Transitive dependencies: in the shared framework; no extra NuGet pulls.
- Single-maintainer risk: none (vendor-backed).
- License: MIT.

**Crypto note:** per the dependency policy, cryptography is explicitly a "build vs. buy → buy" case. Use the framework primitive; do not hand-roll encryption for credentials. **Uncertainty flag:** confirm the recommended key-ring persistence + key-encryption-at-rest configuration for the deployment OS/container at implementation time (e.g. protecting the key ring itself with OS DPAPI/X509 where available).
