---
id: adr-001
title: Ingest contract server resolves identity
status: draft
date: 2026-06-04
---
## Context

In `scratch/`, the agent identifies a device first (`POST /devices/identify` with fingerprints), caches the returned `device_id`, builds fact IDs locally as `Device[{uuid}].Path`, then ingests via `POST /ingest/{deviceId}`. This couples the agent to server-assigned identity: it must cache device IDs, handle cache-cold cases, and round-trip before every cold collection. Owner decision D2 changes this.

**Delta-tracking implication (critic check #1):** the current agent keys its `CollectorDeltaTracker` by `device_id`. If the agent no longer has a `device_id`, what does delta tracking key on?

## Alternatives Considered

1. **Keep identify-first (status quo).** Rejected: extra round-trip, agent must cache and invalidate device IDs, and auto-merge (ADR-002) can change a device's identity underneath a cached ID, forcing cache busting.
2. **Agent generates its own device IDs.** Rejected: agents can't see cross-agent fingerprint overlap, so they'd create duplicate identities the server must reconcile anyway — pushes the merge problem to the wrong layer.
3. **Server resolves identity at ingest (chosen, D2).** Agent submits `{fingerprints[], facts[]}`; server resolves/creates the device and rewrites fact IDs to `Device[{uuid}].Path`.

## Decision

The agent submits `{agent_id, fingerprints[], fact_batches[{fingerprints[], facts[]}]}`. The server resolves each batch's fingerprints to a `device_id` (creating a device if none matches), then rewrites each fact's `id` and `key_values` to embed `Device[{uuid}]`. `POST /devices/identify` is eliminated. The agent never caches a device ID.

**Delta tracking re-keys onto collection-source identity, not server device_id.** The delta tracker is the agent's *private write-reduction cache* — it only ever needed a stable local key per collection source, never the server's resolved identity:
- **Remote targets**: keyed by target address/URL (already the `ResolvedDeviceId ?? target.Address` fallback in `Agent.cs` — the new model makes the fallback the only path).
- **Local host**: keyed by a persistent local key (e.g. a `machine-id` fingerprint persisted to the agent state dir), reused across restarts so a restart does not reset trackers and trigger a full re-send storm.
- **Passive discovery**: keyed by discovery source (`passive:<proto>:<host-key>`).

On failed send, the tracker rolls back so the next cycle retries unsent facts (unchanged).

## Rationale

Separating "what the agent polls" (source identity) from "what the server resolves" (device identity) removes the only reason the agent needed a server device_id. Source keys are locally stable and known before any server contact, so delta tracking works on the very first cycle with no round-trip. Server-side ID rewrite is cheap (string/JSONB construction during ingest) and centralizes identity — the single place auto-merge can also act.

## Consequences

- **Breaking wire change** vs. `scratch/` `FactBatch` (carried `DeviceId`). Acceptable: internal agents only, single operator, agent+server deploy together (assumption #4). Old endpoints removed; versioned under `/api/v1/`.
- Agent code simplifies: drop `_deviceIdCache`, `IdentifyDeviceAsync`, `LocalDeviceIdPath` round-trip; collectors emit against a placeholder root.
- Server ingest gains an ID-rewrite step and an identity-resolution call per batch (one indexed fingerprint lookup — cheap at this scale).
- `facts` response returns resolved `device_id` as informational telemetry only; the agent must NOT persist or key tracking on it.
