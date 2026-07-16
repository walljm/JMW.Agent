-- Devices whose newest fingerprint last_seen is older than $1 days (gone quiet), oldest
-- first, capped at $2. Excludes merged/alias devices. Friendly name best-effort from
-- proj_systems (falls back to the real hostname when no friendly name is known).
SELECT
    d.device_id
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
  , mx.last_seen
FROM
    devices                d
    JOIN LATERAL (
        SELECT
            max(df.last_seen) AS last_seen
        FROM
            device_fingerprints df
        WHERE
            df.device_id = d.device_id
            ) mx
    ON TRUE
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
WHERE
    EXISTS (SELECT 1 FROM live_devices ld WHERE ld.device_id = d.device_id)
  AND mx.last_seen IS NOT NULL
  AND mx.last_seen < now() - make_interval(days => $1)
ORDER BY
    mx.last_seen ASC
  , d.device_id  ASC LIMIT $2
