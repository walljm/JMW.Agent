---
id: adr-006
title: Large-scale mechanisms inherited but deferred
status: draft
date: 2026-06-04
---
## Context

`scratch/architecture.md` was designed for 80K-device networks and specifies several scale mechanisms: **load-balancer device-affinity hashing** (so all facts for a device reach the same instance for EntityStateCache coherence), **horizontal multi-instance scaling**, **monthly `facts_history` partitioning**, and per-cycle delta tracking at 4M+ changed facts. Iteration-2 `constraints.md` (#9) scopes deployment to **a few hundred devices on a single host** with sub-second loads for ≤500 devices (REQ-026). This ADR records which inherited mechanisms apply now vs. are deferred — so they are not silently dropped.

## Alternatives Considered

1. **Implement all 80K-scale mechanisms now.** Rejected: gold-plating for a scale the requirements explicitly exclude (out-of-scope: scalability beyond ~500 devices). Multi-instance affinity adds a load balancer and operational complexity with no payoff at single-host scale.
2. **Delete the mechanisms from the design.** Rejected: they are correct and may be needed if scale grows; deleting loses the design rationale.
3. **Keep but defer, with explicit re-entry points (chosen).**

## Decision

At iteration-2 scale, run a **single server instance**. This makes the per-instance EntityStateCache coherent **by definition** — there is no second instance to diverge from, so the device-affinity / load-balancer-hashing mechanism is **not needed** and is deferred. Likewise:
- **Multi-instance horizontal scaling**: deferred. One ASP.NET Core host.
- **`facts_history` partitioning**: deferred (the schema notes it as a future option; not enabled now).
- **Delta tracking**: **retained** — still the primary write-reduction mechanism and now re-keyed onto source identity (ADR-001). It is useful at any scale.
- **DB write chunking (~10K facts/round-trip)** and **route-table exclusion from projections** (DEC-004): **retained** — they bound lock duration and cardinality regardless of scale.

## Rationale

A single instance dissolves the only reason affinity existed (cache coherence) and removes the event-driven-batch-vs-affinity tension (critic check #2) entirely. The scale constraint (#9) is what *permits* this simplification — it is not a conflict with D1–D10, which govern contract/identity/agent/deployment, not scale.

## Consequences

- EntityStateCache runs normally on one instance; no `maxCacheEntries: 0` fallback needed.
- If the fleet later exceeds the supported scale, re-enable: (a) `facts_history` monthly partitioning, (b) multi-instance + LB device-affinity hashing, (c) revisit delta-tracking throughput. Re-entry points recorded here.
- Single instance is a SPOF — acceptable for a non-HA monitoring tool (out-of-scope: HA/SLA).
