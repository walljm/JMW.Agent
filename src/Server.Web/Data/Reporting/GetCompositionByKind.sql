-- Live-device counts by device kind (from proj_devices). NULL groups together and is labelled
-- by the caller. Excludes merged/alias devices.
SELECT
    pd.kind
  , count(*) AS COUNT
FROM
    live_devices d
    LEFT JOIN proj_devices pd
ON pd.device = d.device_id::text
GROUP BY
    pd.kind
ORDER BY
    COUNT   DESC
  , pd.kind ASC
