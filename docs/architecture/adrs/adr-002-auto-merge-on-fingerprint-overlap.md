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
3. **Auto-merge only on high-confidence fingerprints (serial/uuid/machine-id), flag on weak ones (mac/router-id).** Considered as a safety refinement; not adopted to keep behavior simple per D1. Mitigation instead: normalization already rejects the most dangerous weak fingerprints (LA/multicast MACs, nil UUIDs, placeholder serials), shrinking the false-merge surface. Manual merge (REQ-053) handles the rare wrong-merge by re-splitting via operator action.

## Decision

Auto-merge immediately at ingest when incoming fingerprints link two or more existing device records. Merge algorithm (single transaction):
1. Choose the **oldest** device (`created_at ASC`) as survivor.
2. Repoint losers' `device_fingerprints.device_id` and `facts_history` device keys (`id`, `key_values->>'Device'`) and `proj_*` rows to the survivor.
3. Insert `device_aliases(alias_device_id, survivor_device_id)` for each loser so stale references resolve forward.
4. Set `devices.merged_from` on the survivor; update `updated_at`.
5. Emit an audit event (REQ-027).

Manual merge (REQ-053) and promotion (REQ-036) reuse this merge primitive.

## Rationale

Single-operator, small-to-medium fleet (constraints #9) means the cost of a rare wrong merge (operator re-splits) is lower than the standing cost of fragmented device records and a manual reconciliation queue. Centralizing merge at ingest (server-side, ADR-001) makes it atomic with identity resolution.

## Consequences

- Supersedes `HandleSplitAsync`'s conservative branch; that method now performs the merge.
- New `device_aliases` table; lookups by a merged-away `device_id` must resolve forward (handled in COMP-004 / API-002).
- Merge rewrites `facts_history` keys — must be transactional and chunked for devices with large histories (route tables stored in history, DEC-004).
- **Risk accepted**: duplicate hardware serials could merge two real devices. Mitigations: normalization rejects placeholder/invalid identifiers; manual re-split available; audit trail records every merge.
