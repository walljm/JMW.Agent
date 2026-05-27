# JMW Agent — System Architecture

Network monitoring and inventory system with distributed agents collecting data from multiple sources and a central server that normalizes, correlates, and presents a unified view of all network devices and infrastructure.

## Design Principles

1. **Single pipeline, many sources.** Every observation — whether from an agent's ARP scan, a DHCP server's lease table, or a user's manual input — flows through the same processing stages. No source gets bespoke database access.

2. **Entities at multiple scales.** The data model spans networks (large) through hardware and systems (medium) down to interfaces, services, and disks (small). Relationships between scales are explicit, not inferred at query time.

3. **Observation vs. derived state.** Raw observations are immutable records of "source X told us Y at time T." Derived state (canonical hostname, device kind, network membership) is computed from observations by deterministic rules. The presentation layer reads derived state only.

4. **Priority-aware merging.** When multiple sources report conflicting values for the same field, a fixed priority table determines the winner. The losing value is preserved in observation history, not discarded.

5. **Confidence-tracked identity.** For managed hosts (running our agent), identity is definitive. For unmanaged devices, identity is inferred from correlation evidence, and the system is explicit about confidence levels.

6. **Separation of concerns.** Collection (agents) is decoupled from processing (pipeline) is decoupled from presentation (UI/API). Each can evolve independently.

7. **Wire efficiency by design.** Per-cycle agent traffic is the dominant ongoing cost of the system. Payloads are compressed end-to-end, identity is sent once and referenced by short keys thereafter, unchanged sub-payloads are hash-skipped, and per-cycle requests are coalesced into a single round-trip. The entity model and pipeline are written so these optimizations are invariant-preserving, not bolted on. See `agent-lifecycle.md` → Wire Efficiency.

## Document Index

| Document | Covers |
|----------|--------|
| [Entity Model](entity-model.md) | All entities at every scale: networks, hardware, systems, interfaces, services, disks. Identity rules, relationships, lifecycle. |
| [Data Pipeline](data-pipeline.md) | The five processing stages: Ingest → Identify → Merge → Derive → Store. How observations become entity state. |
| [Observation Sources](observation-sources.md) | Every source adapter: what it collects, what observations it produces, priority assignments. Agent discovery, agent inventory, terrain, future sources. |
| [Metrics & Alerting](metrics-and-alerting.md) | Time-series collection, storage, rollup strategy, alert rule evaluation, notification delivery. |
| [Agent Lifecycle](agent-lifecycle.md) | Agent identity, registration, approval, transport, heartbeat, auto-update, subsystem configuration. |
| [Infrastructure](infrastructure.md) | Authentication, TLS, configuration management, sessions, retention policies, deployment. |
| [Presentation](presentation.md) | UI views, API surface, query patterns. How the read side consumes derived state without coupling to the pipeline. |

## System Context

```
┌─────────────────────────────────────────────────────────────────────┐
│                           LAN / Network                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌──────────────────────┐  │
│  │ Agent 1 │  │ Agent 2 │  │ Agent N │  │ Infrastructure       │  │
│  │ (Linux) │  │ (macOS) │  │ (Win)   │  │ (AdGuard/Technitium) │  │
│  └────┬────┘  └────┬────┘  └────┬────┘  └──────────┬───────────┘  │
│       │             │            │                   │              │
└───────┼─────────────┼────────────┼───────────────────┼──────────────┘
        │             │            │                   │
        ▼             ▼            ▼                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         JMW Server                                   │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    Data Pipeline                              │   │
│  │  Ingest → Identify → Merge → Derive → Store                 │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    SQLite Store                               │   │
│  │  Hardware │ Systems │ Interfaces │ Observations │ Metrics     │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │               Presentation Layer (UI + API)                  │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

## Technology Constraints

| Constraint | Reason |
|------------|--------|
| Single Go binary (server and agent) | Zero-dependency deployment to NAS, Pi, laptop |
| SQLite (pure-Go, no CGO) | Single file, ARM-friendly, no external DB |
| No ORM | Direct SQL for control and performance |
| Stdlib-first | Minimize supply chain risk |
| Server-rendered HTML + vanilla JS | No build pipeline, no node_modules |
| Statically linked (`CGO_ENABLED=0`) | Distroless Docker base image |
