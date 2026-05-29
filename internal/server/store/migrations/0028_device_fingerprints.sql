-- 0028 device fingerprints: identity resolution layer
--
-- A device (hardware row) can be identified by many fingerprints: MACs, serial
-- numbers, engine UUIDs, etc. The resolver checks all known fingerprints before
-- creating a new device, and registers new fingerprints as they are discovered.

CREATE TABLE IF NOT EXISTS device_fingerprints (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    hardware_id TEXT NOT NULL REFERENCES hardware(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,            -- 'mac', 'serial:system', 'serial:board', 'docker_engine_id'
    value TEXT NOT NULL,           -- normalized fingerprint value
    source TEXT NOT NULL DEFAULT '',  -- what reported it: 'agent', 'discovery', 'terrain-dhcp', etc.
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_fingerprints_kind_value ON device_fingerprints(kind, value);
CREATE INDEX IF NOT EXISTS idx_fingerprints_hardware ON device_fingerprints(hardware_id);

-- Seed MAC fingerprints from existing interfaces.
INSERT OR IGNORE INTO device_fingerprints (hardware_id, kind, value, source, first_seen_at, last_seen_at)
SELECT hardware_id, 'mac', mac, '', first_seen_at, last_seen_at
FROM interfaces
WHERE mac IS NOT NULL AND mac != '';

-- Seed serial:system fingerprints from existing hardware rows that have both
-- system_vendor and system_serial set.
INSERT OR IGNORE INTO device_fingerprints (hardware_id, kind, value, source, first_seen_at, last_seen_at)
SELECT id, 'serial:system', system_vendor || char(0) || system_serial, '', first_seen_at, last_seen_at
FROM hardware
WHERE system_serial IS NOT NULL AND system_serial != ''
  AND system_vendor IS NOT NULL AND system_vendor != '';
