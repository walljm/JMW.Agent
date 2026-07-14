-- Live-device counts by OS family (from proj_systems). NULL groups together and is labelled
-- by the caller. Excludes merged/alias devices.
SELECT
    s.os_family
  , count(*) AS count
FROM
    live_devices           d
    LEFT JOIN proj_systems s ON s.device = d.device_id::text
GROUP BY
    s.os_family
ORDER BY
    count DESC
  , s.os_family ASC
