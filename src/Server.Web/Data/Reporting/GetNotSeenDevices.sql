-- Devices whose newest sighting (devices.last_seen, migration 0105) is older than $1 days
-- (gone quiet), oldest first, capped at $2. Excludes merged/alias devices. Friendly name
-- best-effort from proj_systems (falls back to the real hostname when no friendly name is known).
SELECT
    d.device_id
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
  , d.last_seen
FROM
    -- Base table, not the live_devices view: view columns lose NOT NULL metadata, which fails
    -- the generated schema validator's nullability check on device_id. The EXISTS keeps the
    -- alias-hiding semantics.
    devices                d
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
WHERE
    EXISTS (SELECT 1 FROM live_devices ld WHERE ld.device_id = d.device_id)
  AND d.last_seen IS NOT NULL
  AND d.last_seen < now() - make_interval(days => $1)
ORDER BY
    d.last_seen ASC
  , d.device_id ASC LIMIT $2
