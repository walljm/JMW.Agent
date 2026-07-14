-- PERF-004: composite index on device_fingerprints(device_id, source) to support
-- the source-filter EXISTS in ListHosts.sql without a full device-fingerprint scan.
CREATE INDEX if NOT EXISTS ix_device_fingerprints_device_source
    ON device_fingerprints (device_id, SOURCE)
    WHERE SOURCE IS NOT NULL;
