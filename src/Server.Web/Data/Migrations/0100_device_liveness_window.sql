-- device_liveness_settings: the wall-clock window after which a device that has not been observed
-- is HIDDEN from live inventory views (device list, dashboard summary tiles, composition
-- breakdowns) -- but never deleted. A hidden device stays fully reachable by id and in every
-- history / liveness-awareness surface (device detail, entity history, recent activity, the
-- Not-Seen and Network "quiet" panels). On busy networks -- home ones especially -- guest devices
-- appear briefly then vanish; the window keeps the live inventory to what is actually present
-- without losing the record. Mirrors agent_liveness_settings: exactly one row, id always TRUE.
--
-- "Seen" = any device_fingerprint observed within the window. device_fingerprints.last_seen is
-- stamped UNCONDITIONALLY on every resolution (ARP / ping / discovery / agent report --
-- DeviceRegistry.UpsertFingerprintsAsync), so it bumps each cycle for a device that is present even
-- when nothing about it changed. Projection updated_at columns (e.g. proj_systems.updated_at) only
-- move on a data change, so they go stale on a live-but-static device -- which is why last_seen,
-- not updated_at, is the one reliable per-device recency signal.
CREATE TABLE device_liveness_settings (
    id boolean NOT NULL PRIMARY KEY DEFAULT TRUE CHECK (id),
    window_hours int NOT NULL DEFAULT 24 CHECK (window_hours > 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO device_liveness_settings (id) VALUES (TRUE);

-- Live AND recently-seen devices. live_devices already drops merged-away aliases; visible_devices
-- adds the freshness gate. Live/default inventory reads select FROM visible_devices; history and
-- liveness-awareness surfaces keep reading live_devices/devices so a hidden device stays recorded
-- and still countable as "quiet". EXISTS short-circuits on the first in-window fingerprint (indexed
-- by device_id), so it never has to aggregate the whole sighting set.
--
-- Fail-open on missing data: a device is hidden only when it HAS sighting records and all of them
-- are older than the window. A device with no fingerprints at all is left visible rather than
-- hidden — we never suppress a device we have no recency evidence for (over-hiding is the worse
-- error for a declutter filter). In practice every device carries >= 1 fingerprint (stamped on the
-- resolution that created it), so this only guards the degenerate zero-sighting case.
CREATE OR REPLACE VIEW visible_devices AS
SELECT d.*
FROM live_devices d
WHERE NOT EXISTS (
        SELECT 1 FROM device_fingerprints df WHERE df.device_id = d.device_id
      )
   OR EXISTS (
        SELECT 1
        FROM device_fingerprints df
        WHERE df.device_id = d.device_id
          AND df.last_seen >= now() - make_interval(hours => (SELECT window_hours FROM device_liveness_settings WHERE id))
      );
