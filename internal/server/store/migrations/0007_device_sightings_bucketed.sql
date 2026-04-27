-- 0007: bucket device_sightings by (device, agent, ip, mac, method) instead of
-- one row per event. Keeps storage O(devices*agents*tuples) instead of growing
-- forever. Existing rows are discarded; they were a high-churn event log.

DROP TABLE IF EXISTS device_sightings;

CREATE TABLE device_sightings (
    device_id     TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    seen_by_agent TEXT NOT NULL DEFAULT '',
    ip            TEXT NOT NULL DEFAULT '',
    mac           TEXT NOT NULL DEFAULT '',
    method        TEXT NOT NULL DEFAULT '',  -- arp | mdns | ping
    first_seen_at TEXT NOT NULL,
    last_seen_at  TEXT NOT NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (device_id, seen_by_agent, ip, mac, method)
);
CREATE INDEX IF NOT EXISTS idx_device_sightings_last_seen ON device_sightings(device_id, last_seen_at DESC);
