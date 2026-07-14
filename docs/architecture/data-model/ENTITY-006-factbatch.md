---
id: ENTITY-006
name: FactBatch
status: draft
---
## Fields
**Wire format only — NOT a DB table.** Iteration-2 contract (ADR-001, D2). The agent submits this; the server resolves identity and rewrites fact IDs. Replaces the old `FactBatch { AgentId, DeviceId, CollectedAt, Facts[] }`.

Top-level request body of `POST /api/v1/agent/facts`:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| agent_id | UUID | yes | Verified server-side against the authenticated API key. |
| collected_at | TIMESTAMPTZ | yes | Cycle timestamp. |
| fact_batches | array | yes | One per device/source observed this cycle. Each element: `{ fingerprints[], facts[] }`. |

Each `fact_batch` element:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| fingerprints | array of `{type, value}` | yes (≥1) | Identifies the device; server resolves to a `device_id`. Invalid ones dropped; empty-after-normalization → batch rejected. |
| facts | array of Fact | yes | Facts emitted against a placeholder device root; server rewrites IDs to `Device[{uuid}].Path`. ≤50 000 per batch. |

Transport: **gzip-compressed JSON**. Fact IDs share long common prefixes → 80–90% compression.

## Relationships
- `fingerprints[]` → resolved to Device (ENTITY-001) + DeviceFingerprint (ENTITY-002) by COMP-004.
- `facts[]` → become rows in `facts_history` (after ID rewrite) and update `proj_*`.

## PII Fields
Facts may carry hostnames, usernames, session data. In transit over the agent→server channel (HTTPS desirable, assumption #8). Not persisted in this shape.

## Ownership
Produced by Agent (COMP-001) / Agent.PassiveDiscovery (COMP-002); consumed by Server.Ingest (COMP-003).

## Migration Strategy
**Wire-contract change** (not a schema migration). Breaking change vs. the `scratch/` `FactBatch` (which carried `DeviceId`). Versioned under `/api/v1/`; the old `/devices/identify` + `/ingest/{deviceId}` path is removed (no external consumers — single-operator, internal agents, assumption #4). Agents and server deploy together; no dual-run window required. Documented in api-spec.md changelog.
