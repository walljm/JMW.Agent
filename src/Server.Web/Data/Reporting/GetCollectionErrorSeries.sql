-- Collection errors bucketed hourly over the last 24h, for the error-rate sparkline.
-- Empty buckets are absent; the caller fills gaps with zero.
SELECT
    date_trunc('hour', cycle_at)  AS bucket
  , coalesce(sum(error_count), 0) AS errors
FROM
    agent_cycles
WHERE
    cycle_at >= now() - INTERVAL '24 hours'
GROUP BY
    date_trunc(
    'hour'
  , cycle_at)
ORDER BY
    bucket ASC
