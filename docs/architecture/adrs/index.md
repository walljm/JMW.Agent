---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Architecture Decision Records — Index

| ADR | Title | Decision (one line) | Owner decision | Status |
|-----|-------|---------------------|----------------|--------|
| [adr-001](adr-001-ingest-contract-server-resolves-identity.md) | Ingest contract: server resolves identity | Agent submits `{fingerprints, facts}`; server resolves device_id and rewrites fact IDs; delta tracking re-keyed onto source identity | D2 | accepted |
| [adr-002](adr-002-auto-merge-on-fingerprint-overlap.md) | Auto-merge on fingerprint overlap | Merge immediately at ingest when fingerprints link multiple devices; `device_aliases` redirects | D1 | accepted |
| [adr-003](adr-003-agent-as-long-running-daemon-with-passive-discover.md) | Agent as long-running daemon + passive discovery | In-process event-driven listeners; raw-socket graceful degradation | D3, D5 | accepted |
| [adr-004](adr-004-razor-pages-plus-htmx-with-polling-liveness.md) | Razor Pages + HTMX, polling | Server-rendered UI, HTMX partials poll; no SSE/WebSockets | D6 | accepted |
| [adr-005](adr-005-single-postgresql-store-for-all-server-data.md) | Single PostgreSQL store | One DB for facts, projections, registry, config, credentials, auth, audit | D9 | accepted |
| [adr-006](adr-006-large-scale-mechanisms-inherited-but-deferred.md) | Large-scale mechanisms deferred | Single instance; affinity/partitioning deferred; delta tracking + chunking + route-exclusion retained | (scale) | accepted |
| [adr-007](adr-007-roslyn-analyzers-as-mandatory-static-analysis.md) | Roslyn analyzers mandatory | NET analyzers, warnings-as-errors, Nullable enable, in CI | (task-card) | accepted |
| [adr-008](adr-008-adr-008-server-side-discovery-materializer-synthes.md) | Server-side Discovery Materializer | Post-ingest pass (COMP-010) materializes `discovered` device records from foreign-device fingerprints (ARP/DHCP/LLDP) in projections; auto-merge (ADR-002) applies on subsequent overlap | (discovery) | accepted |

## Superseded / changed from `scratch/`

- adr-001 **supersedes** the `scratch/architecture.md` "Collection Pipeline" identify-first flow and the `FactBatch{DeviceId}` wire format.
- adr-002 **supersedes** `DeviceRegistry.HandleSplitAsync`'s conservative flag-don't-merge stance.
- adr-006 records that the 80K-device scale mechanisms in `scratch/architecture.md` (load-balancer device affinity, multi-instance, partitioning) are **inherited but deferred**, not deleted.
