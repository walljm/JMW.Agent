---
id: COMP-004
name: Server Device Registry
status: draft
---
## Responsibility

Fingerprint resolution and device lifecycle (`Server.DeviceRegistry`). Iteration-2 behavior change per ADR-002 (D1): **auto-merge on fingerprint overlap**, superseding the current conservative "flag-don't-merge" stance.

`ResolveDeviceId(normalizedFingerprints) → (device_id, isNew, mergedFrom[])`:

- **No match** → create a new `devices` row (`management_status` defaulted by caller: `managed` for collection targets, `discovered` for passive discovery), store all valid fingerprints, return new `device_id`.
- **One match** → associate any new fingerprints with the matched device, return its `device_id`.
- **Multi-match (split)** → the incoming fingerprints link two or more existing device records. **Auto-merge immediately** (D1): choose the oldest device as the survivor, repoint the losers' fingerprints, fact/metrics history, `change_events`, `incidents`, `agents.device_id`, and `proj_*` rows to the survivor, write `device_aliases` rows (`alias_device_id → survivor_device_id`) so any stale references resolve forward, and record `merged_from[]` on the survivor. Emit an audit event (REQ-027) and a `change_events` row (`event_type='merged'`). Manual merge (REQ-053) and promotion (REQ-036) reuse the same merge primitive.

The whole resolve (match lookup through whichever branch runs) is one transaction, serialized against every other resolve/merge/delete by a fixed-key `pg_advisory_xact_lock` — closes a TOCTOU race between the match lookup and the writes that follow it.

Fingerprint normalization (existing `FingerprintNormalizer`) is applied to all incoming fingerprints; invalid ones are dropped. `device_fingerprints.(fp_type, fp_value)` PK enforces one device per fingerprint.

There is no re-split: once merged, the loser's original `devices` row (`created_at`, `management_status`) is gone and fingerprint provenance isn't logged, so a true reversal isn't reconstructable. The fallback for a bad merge is `DeleteDeviceAsync` (see below).

## Interfaces

- **← COMP-003 Ingest (in-process)**: `ResolveDeviceId(fingerprints, defaultStatus)`.
- **← COMP-007 Web admin (in-process)**: `Merge(survivorId, loserId)` (manual merge REQ-053), `Promote(deviceId)` (discovered→managed REQ-036), `DeleteDeviceAsync(deviceId)` (manual fallback for a bad merge — hard-deletes the device and everything attached to it; does not attempt to reconstruct a prior state).
- **→ COMP-009 Database**: reads/writes `devices`, `device_fingerprints`, `device_aliases`; the merge transaction also rewrites `facts_history`/`metrics_raw` keys, `change_events.entity_id`, `incidents.entity_id` (resolving a same-type open-incident collision as superseded rather than deleting it), `agents.device_id`, and projection rows for the merged device. `DeleteDeviceAsync` hard-deletes/nulls the same set of tables instead of repointing them.

## Dependencies

- COMP-009 Database

## Integration Test Boundaries

- **Auto-merge on overlap (DB, transactional)**: create devices A and B with disjoint fingerprints; submit a batch whose fingerprints overlap both; assert A∪B merged into the older, `device_aliases` written, `merged_from[]` populated, loser fingerprints/history/change_events/incidents/agents.device_id/projections repointed, and a `merged` change_event recorded. Assert the merge is atomic (rollback on failure leaves both devices intact).
- **Alias forward-resolution (DB)**: after merge, a lookup by the loser's old `device_id` resolves to the survivor via `device_aliases`.
- **Idempotent re-association (DB)**: re-submit known fingerprints for an existing device; assert no duplicate fingerprint rows and no spurious merge.
- **Normalization drop (in-process)**: submit a locally-administered MAC and a nil UUID; assert both dropped, resolution uses only valid fingerprints.
- **Delete fallback (DB)**: delete a device with fingerprints/projections/history/incidents/change_events/an aliased-in loser; assert all of it is gone and the aliased-in loser's `device_aliases` row is cleaned up too.
