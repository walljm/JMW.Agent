-- Live-device counts by discovery source (how devices are being seen). A device observed by N
-- sources is counted in N buckets, so totals exceed the live-device count — this is a per-source
-- reach breakdown, not a partition. Uses the shared device_discovery_sources view.
SELECT
    ds.source
  , count(DISTINCT ds.device_id) AS COUNT
FROM
    device_discovery_sources ds
    JOIN visible_devices d
    ON d.device_id = ds.device_id
GROUP BY
    ds.source
ORDER BY
    COUNT      DESC
  , ds.source ASC
