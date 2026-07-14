---
id: DEP-003
package: Npgsql
version: latest stable for net10.0
license: PostgreSQL License
adr_ref: adr-005
status: draft
---
## Justification

PostgreSQL driver/ADO.NET provider for .NET. Required for all database access (COMP-009, ADR-005). The architecture mandates the `NpgsqlDataSource` pattern (DotNet guidance) and raw SQL with no ORM (constraints #3) — Npgsql is the de-facto, essentially only production-grade native PostgreSQL provider for .NET, and a database driver is exactly the kind of "impractical to build in-house" dependency the dependency policy permits. Already used in `scratch/` (`DeviceRegistry`, `FactRepository`).

## Health Assessment
- Maintenance activity: actively maintained, frequent releases, tracks new .NET and PostgreSQL versions; part of the .NET Foundation.
- Community adoption: the standard PostgreSQL provider for .NET; very high adoption.
- Transitive dependencies: minimal (a few Microsoft.Extensions / System.* packages); no heavy tree.
- Single-maintainer risk: low — .NET Foundation project with multiple contributors.
- License: PostgreSQL License (permissive, BSD-like).

**Uncertainty flag:** current Npgsql major version compatible with .NET 10 and any advisories not verified via live research here. Recommend confirming the .NET 10-compatible release and CVE status before pinning. Version pin target: latest stable Npgsql supporting .NET 10.
