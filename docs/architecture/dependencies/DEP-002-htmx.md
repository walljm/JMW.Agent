---
id: DEP-002
package: HTMX
version: latest-stable (vendored, SRI-pinned)
license: BSD-2-Clause
adr_ref: adr-004
status: draft
---
## Justification

Client-side hypermedia library for the Razor Pages UI (ADR-004, D6). Enables HTML-partial swaps and polling-based liveness (DEC-005) without an SPA framework or a JS build pipeline. A single small script include — no npm/bundler toolchain — which directly serves constraints #7 (minimal dependencies). Served as a static asset from `Server.Web`; pinned by file (vendored), not via a package manager, to avoid a JS dependency tree.

## Health Assessment
- Maintenance activity: actively maintained, mature 2.x line as of knowledge cutoff.
- Community adoption: widely adopted in server-rendered stacks (Django, Rails, ASP.NET); strong docs.
- Transitive dependencies: **zero** — single self-contained script, no runtime deps.
- Single-maintainer risk: small core team / primary author (Big Sky Software). Moderate bus-factor risk, mitigated by: tiny footprint, no transitive deps, MIT license, and the fact that a vendored copy is fully self-contained (no upgrade pressure if upstream stalls).
- License: BSD-2-Clause / Zero-Clause BSD (permissive).

**Uncertainty flag:** current HTMX major/minor version and any advisories were not verified via live research here. Recommend confirming the latest stable release and integrity hash before vendoring. Version pin target: latest stable HTMX at implementation time, vendored with an SRI hash.
