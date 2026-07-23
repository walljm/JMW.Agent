-- Device[].Interface[].TotalBytes (Rx+Tx combined, computed by TotalBytesDerivation at ingest)
-- history for one device's busiest interface — the Device Summary tab's throughput sparkline.
-- "Busiest" = whichever interface currently has the highest cumulative total, a reasonable proxy
-- for "the primary NIC" over e.g. a rarely-used docker bridge. Bounded only by whatever
-- MetricRetention:StaleAfter has actually kept (partitions past that are dropped outright, not
-- filtered), so no explicit time bound is needed here.
WITH latest_per_iface AS (
    SELECT DISTINCT ON (key_values ->> 'Interface')
        key_values ->> 'Interface' AS iface
      , value_long AS bytes
    FROM
        metrics_raw
    WHERE
        attribute_path = 'Device[].Interface[].TotalBytes'
        AND key_values ->> 'Device' = $1
    ORDER BY
        key_values ->> 'Interface'
      , collected_at DESC
),
busiest AS (
    SELECT iface
    FROM latest_per_iface
    ORDER BY bytes DESC NULLS LAST
    LIMIT 1
)
SELECT
    m.value_long AS bytes
  , m.collected_at
  , busiest.iface AS interface_name
FROM
    busiest
    JOIN metrics_raw m
    ON m.attribute_path = 'Device[].Interface[].TotalBytes'
   AND m.key_values ->> 'Device' = $1
   AND m.key_values ->> 'Interface' = busiest.iface
ORDER BY
    m.collected_at
