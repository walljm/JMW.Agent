---
id: DEC-004
title: Route tables excluded from projection; queried from facts_history
date: 2026-06-04
status: draft
---

## Decision
Route table entries (Vrf[name].Route[prefix]) are NOT projected into a projection table. They are stored only in facts_history and queried directly when needed. Route count and default route presence are projected as summary facts.

## Rationale
Route tables are extremely high-cardinality (100K+ routes per device on large networks). Projecting them would result in massive projection tables with frequent bulk updates, causing severe write amplification. The architecture document explicitly calls out this exclusion.

## Alternatives Considered
- **Projection with partial indexing**: Would still require storing and updating potentially millions of rows.
- **Separate high-cardinality projection store**: Adds architectural complexity.

## Consequences
- The UI cannot show live route tables in the same simple projection-read pattern as other entities.
- A route table view requires a direct facts_history query with appropriate indexing.
- Route summary stats (count, default route present) can still be shown in device detail via projected summary facts.
