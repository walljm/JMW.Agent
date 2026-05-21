-- 0008: track every observed (name, source) per device, and remember which
-- source supplied the canonical hostname so future updates respect priority.

ALTER TABLE devices ADD COLUMN hostname_source TEXT NOT NULL DEFAULT '';

CREATE TABLE device_hostnames (
    device_id     TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    source        TEXT NOT NULL,           -- mdns | nbns | rdns
    first_seen_at TEXT NOT NULL,
    last_seen_at  TEXT NOT NULL,
    count         INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (device_id, name, source)
);
CREATE INDEX IF NOT EXISTS idx_device_hostnames_last_seen ON device_hostnames(device_id, last_seen_at DESC);
