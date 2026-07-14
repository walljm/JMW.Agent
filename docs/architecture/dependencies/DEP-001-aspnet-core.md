---
id: DEP-001
package: ASP.NET Core
version: net10.0 (SDK-bundled)
license: MIT
adr_ref: adr-004
status: draft
---
## Justification

Web framework for `Server.Web` (COMP-007): Razor Pages + HTMX UI, agent/admin/reporting/auth APIs, cookie-session auth, RBAC policies, and the Data Protection key ring (DEP-004). Mandated by constraints #1 (C# .NET 10) and chosen in ADR-004. This is a first-party Microsoft framework shipped with the .NET 10 SDK — not a third-party dependency, recorded here for completeness.

## Health Assessment
- Maintenance activity: Microsoft-maintained, part of the .NET SDK release train (.NET 10 is the constrained runtime). LTS cadence.
- Community adoption: one of the most widely used server frameworks; very large ecosystem.
- Transitive dependencies: bundled in the shared framework (`Microsoft.AspNetCore.App`); no additional NuGet pulls for core use.
- Single-maintainer risk: none (vendor-backed).
- License: MIT.

**Uncertainty flag:** exact .NET 10 GA/patch version and any post-cutoff advisories were not verified via live research in this environment. Recommend the team confirm the current .NET 10 patch level and security advisories before pinning. Version pin target: the latest .NET 10 SDK/runtime patch at implementation time.
