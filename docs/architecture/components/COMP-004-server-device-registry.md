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
- **Multi-match (split)** → the incoming fingerprints link two or more existing device records. **Auto-merge immediately** (D1): choose the oldest device as the survivor, repoint the losers' fingerprints and fact history to the survivor, write `device_aliases` rows (`alias_device_id → survivor_device_id`) so any stale references resolve forward, and record `merged_from[]` on the survivor. Emit an audit event (REQ-027). Manual merge (REQ-053) and promotion (REQ-036) reuse the same merge primitive.

Fingerprint normalization (existing `FingerprintNormalizer`) is applied to all incoming fingerprints; invalid ones are dropped. `device_fingerprints.(fp_type, fp_value)` PK enforces one device per fingerprint.

## Interfaces

- **← COMP-003 Ingest (in-process)**: `ResolveDeviceId(fingerprints, defaultStatus)`.
- **← COMP-007 Web admin (in-process)**: `Merge(survivorId, loserId)` (manual merge REQ-053), `Promote(deviceId)` (discovered→managed REQ-036).
- **→ COMP-009 Database**: reads/writes `devices`, `device_fingerprints`, `device_aliases`; the merge transaction also rewrites `facts_history.key_values`/`id` device keys and re-points projection rows for the merged device.

## Dependencies

- COMP-009 Database

## Integration Test Boundaries

- **Auto-merge on overlap (DB, transactional)**: create devices A and B with disjoint fingerprints; submit a batch whose fingerprints overlap both; assert A∪B merged into the older, `device_aliases` written, `merged_from[]` populated, loser fingerprints/history repointed. Assert the merge is atomic (rollback on failure leaves both devices intact).
- **Alias forward-resolution (DB)**: after merge, a lookup by the loser's old `device_id` resolves to the survivor via `device_aliases`.
- **Idempotent re-association (DB)**: re-submit known fingerprints for an existing device; assert no duplicate fingerprint rows and no spurious merge.
- **Normalization drop (in-process)**: submit a locally-administered MAC and a nil UUID; assert both dropped, resolution uses only valid fingerprints.
