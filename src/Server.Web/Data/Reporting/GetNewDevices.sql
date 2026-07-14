-- Newest devices first observed within $1 days, newest first, capped at $2.
-- Excludes merged/alias devices. Hostname is best-effort from proj_systems.
SELECT
    d.device_id
  , s.hostname
  , d.management_status
  , d.created_at
FROM
    devices                d
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
WHERE
    EXISTS (SELECT 1 FROM live_devices ld WHERE ld.device_id = d.device_id)
  AND d.created_at >= now() - make_interval(days => $1)
ORDER BY
    d.created_at DESC
  , d.device_id  ASC LIMIT $2
