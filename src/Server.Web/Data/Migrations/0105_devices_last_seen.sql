-- devices.last_seen: the newest fingerprint sighting, denormalized onto the registry row
-- (docs/plans/context-derivations.md §3.4). Previously every reader computed
-- max(device_fingerprints.last_seen) per device at query time (DeviceListApi's BaseCte,
-- ListDevices.sql, GetNotSeenDevices.sql, visible_devices' EXISTS probe) — an aggregate that can
-- never drive an index-ordered sort and re-does the same work on every read. The value changes on
-- effectively every resolution, so it is deliberately NOT a fact/context-derivation (pure write
-- amplification in facts_history — same reasoning that moved metrics to metrics_raw); instead it
-- is stamped at the two places device_fingerprints.last_seen itself moves:
--   1. DeviceRegistry.UpsertFingerprintsAsync (every resolution, same transaction), and
--   2. StampObservedMacLastSeen.sql (the materializer's passive-liveness forward bump),
-- plus merge-time absorption in MergeLosersAsync (survivor takes GREATEST of both sides).
-- Monotonic max-of-observations: retention pruning old fingerprints never moves it backward.
ALTER TABLE devices ADD COLUMN last_seen timestamptz;

UPDATE devices d
SET last_seen = mx.last_seen
FROM (
    SELECT device_id, max(last_seen) AS last_seen
    FROM device_fingerprints
    GROUP BY device_id
) mx
WHERE mx.device_id = d.device_id;

-- Devices report "last_seen" sort drives visible_devices (a star-view over this table).
CREATE INDEX devices_last_seen_idx ON devices (last_seen, device_id);

-- Postgres freezes a SELECT * view's column list at creation time, so both star-views over
-- devices must be re-created to expose the new column. live_devices body is unchanged from
-- 0047 (alias-hiding predicate); CREATE OR REPLACE is valid because last_seen appends at the
-- end of the column list.
CREATE OR REPLACE VIEW live_devices AS
SELECT d.*
FROM devices d
WHERE NOT EXISTS (
    SELECT 1 FROM device_aliases da WHERE da.alias_device_id = d.device_id
);

-- visible_devices semantics preserved exactly from 0100, now as a plain column comparison:
-- fail-open (a device with no sighting evidence — last_seen IS NULL — is never hidden),
-- otherwise hidden only when its newest sighting is older than the liveness window.
CREATE OR REPLACE VIEW visible_devices AS
SELECT d.*
FROM live_devices d
WHERE d.last_seen IS NULL
   OR d.last_seen >= now() - make_interval(hours => (SELECT window_hours FROM device_liveness_settings WHERE id));
