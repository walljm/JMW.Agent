---
id: COMP-005
name: Server Analysis
status: draft
---
## Responsibility

Normalization and derivation engine (`Analysis` project, `AnalysisEngine`). Unchanged in structure from `scratch/`; documented here for completeness and traceability.

- **Normalizers**: single-value, context-free transforms registered against an `attribute_path` pattern (e.g. `"1Gbps"` → `1_000_000_000L`; MAC string → normalized hex; `"unknown"` → drop). Run on the agent before submission.
- **Derivations**: multi-value computations with declared inputs/outputs and inferred scope (e.g. `Interface.TotalBytes = Rx + Tx`, `Filesystem.UsedPercent`, `System.MemUsedPercent`). Run in topological order. Missing inputs → no output, no error.
- **Provenance not stored**: the fact ID is identity; the same fact may be observed on one device and derived on another (REQ-035 multi-source merging relies on this — last-writer-by-collected-at wins in history dedup).
- Most analysis runs **agent-side** (keeps the wire payload canonical). Cross-device derivations that need facts from multiple devices/sources run **server-side** after ingest resolves identity.

## Interfaces

- **Agent-side (in-process, COMP-001)**: `AnalysisEngine.Run(rawFacts) → canonicalFacts` before delta tracking.
- **Server-side (in-process, COMP-003)**: cross-device derivation pass after ID rewrite, before projection routing.
- `BuildId(template, contextFact)` fills scope keys into an `attribute_path` template.

## Dependencies

- COMP-003 Server Ingest (server-side derivation host)
- COMP-001 Agent (agent-side normalization host)

## Integration Test Boundaries

- **Normalizer correctness (unit/in-process)**: feed raw `"1Gbps"`, assert `1_000_000_000`; feed `"unknown"`, assert fact dropped.
- **Derivation ordering (in-process)**: supply inputs out of dependency order; assert topological execution produces correct layered outputs.
- **Multi-source merge (DB)**: ingest the same fact for one device from two sources (SSH then SNMP) with differing `collected_at`; assert history dedup keeps the latest value and projections reflect it (REQ-035).
