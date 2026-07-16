---
id: adr-002
title: Auto-merge on fingerprint overlap
status: draft
date: 2026-06-04
---
## Context

`scratch/DeviceRegistry.HandleSplitAsync` takes a conservative stance: when incoming fingerprints match multiple existing devices, it does **not** merge — it returns the oldest device as canonical and leaves a TODO to raise a data-quality alert for manual review. The stated fear: duplicate serials in the wild could collapse genuinely distinct devices. Owner decision D1 overrides this.

## Alternatives Considered

1. **Flag-don't-merge (status quo).** Rejected by owner (D1): leaves split-brain device records that fragment fact history and reporting until an operator manually intervenes; for a single-operator small fleet the manual queue is friction with little payoff.
2. **Auto-merge on any single shared fingerprint.** This is the owner's decision (D1): fingerprint overlap = same device.
3. **Auto-merge only on high-confidence fingerprints (serial/uuid/machine-id), flag on weak ones (mac/router-id).** Considered as a safety refinement; not adopted to keep behavior simple per D1. Mitigation instead: normalization already rejects the most dangerous weak fingerprints (LA/multicast MACs, nil UUIDs, placeholder serials), shrinking the false-merge surface. A wrong merge is handled by deleting the merged device outright (see Consequences) rather than by re-splitting it.

## Decision

Auto-merge immediately at ingest when incoming fingerprints link two or more existing device records. The whole resolve (match lookup + whichever of create/update/merge follows) runs in one transaction serialized by a fixed-key `pg_advisory_xact_lock`, closing a TOCTOU race where two concurrent resolves with overlapping new fingerprints could otherwise see the same matches and independently pick different survivors. Merge algorithm within that transaction:
1. Choose the **oldest** device (`created_at ASC`) as survivor.
2. Repoint losers' `device_fingerprints.device_id`, `facts_history`/`metrics_raw` device keys (`id`, `key_values->>'Device'`), `change_events.entity_id`, `incidents.entity_id`, `agents.device_id`, and `proj_*` rows to the survivor. `incidents` has a partial unique index on open rows per (entity, type); a collision (both loser and survivor have an open incident of the same type) resolves the loser's as superseded (`resolution = 'merged'`) rather than deleting it, preserving its audit trail.
3. Insert `device_aliases(alias_device_id, survivor_device_id)` for each loser so stale references resolve forward.
4. Set `devices.merged_from` on the survivor; update `updated_at`.
5. Emit an audit event (REQ-027) and, once the transaction commits, a `change_events` row (`event_type = 'merged'`) — the same event manual merge already recorded, now emitted uniformly regardless of entry point.

Manual merge (REQ-053) and promotion (REQ-036) reuse this merge primitive.

## Rationale

Single-operator, small-to-medium fleet (constraints #9) means the cost of a rare wrong merge is lower than the standing cost of fragmented device records and a manual reconciliation queue. Centralizing merge at ingest (server-side, ADR-001) makes it atomic with identity resolution. Full serialization via a single fixed advisory-lock key (rather than partial/fingerprint-scoped locking) is deliberately simple: at this fleet scale, serializing the whole resolve costs nothing in practice.

## Consequences

- Supersedes `HandleSplitAsync`'s conservative branch; that method now performs the merge.
- New `device_aliases` table; lookups by a merged-away `device_id` must resolve forward (handled in COMP-004 / API-002).
- Merge rewrites `facts_history` keys — must be transactional and chunked for devices with large histories (route tables stored in history, DEC-004).
- **Risk accepted**: duplicate hardware serials could merge two real devices. Mitigations: normalization rejects placeholder/invalid identifiers; audit trail records every merge. **No re-split exists, and none is reconstructable as designed**: the merge hard-deletes the loser's `devices` row (its `created_at`/`management_status` are gone) and reassigns `device_fingerprints` in place with no provenance log of which fingerprints originally belonged to which loser. The fallback for a bad merge is `DeviceRegistry.DeleteDeviceAsync` — a manual admin action that hard-deletes the merged device and everything attached to it (fingerprints, projections, history, incidents, change events), so the next observation of that physical entity resolves as a fresh device. It does not restore the pre-merge state, and if the same fingerprint overlap caused the bad merge, deleting alone won't prevent it from recurring on rediscovery — pair it with excluding the offending fingerprint (`excluded_fingerprints`) when that's the case.
