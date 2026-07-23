-- Service[].DNS.Stats.BlockedPct history for one service, oldest first — the Service Summary
-- tab's DNS-activity trend. facts_history is dedup-on-write (a row only lands here when the
-- value actually changed), so points are unevenly spaced in time; the renderer treats each value
-- as holding steady until the next point (a step chart), which is what actually happened, rather
-- than assuming a fixed polling interval or smoothly interpolating between changes.
--
-- before_window seeds the chart with whatever value was last set before the 30-day window, so a
-- service whose blocked% simply hasn't changed recently still shows a flat line instead of no
-- data at all — a real risk with dedup-on-write history, where "no row" can mean either "never
-- collected" or "collected once, long ago, never changed since".
WITH before_window AS (
    SELECT COALESCE(value_double, value_long::double precision) AS value, collected_at
    FROM facts_history
    WHERE attribute_path = 'Service[].DNS.Stats.BlockedPct'
      AND key_values ->> 'Service' = $1
      AND collected_at < now() - INTERVAL '30 days'
    ORDER BY collected_at DESC
    LIMIT 1
),
in_window AS (
    SELECT COALESCE(value_double, value_long::double precision) AS value, collected_at
    FROM facts_history
    WHERE attribute_path = 'Service[].DNS.Stats.BlockedPct'
      AND key_values ->> 'Service' = $1
      AND collected_at >= now() - INTERVAL '30 days'
)
SELECT value, collected_at FROM before_window
UNION ALL
SELECT value, collected_at FROM in_window
ORDER BY collected_at ASC
