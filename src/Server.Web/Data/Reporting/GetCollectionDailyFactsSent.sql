-- Facts sent per day, summed across every agent's cycles, over the last $1 days (inclusive of
-- today). Raw send volume — includes heartbeat-only cycles, but those always contribute 0
-- facts_sent so summing over them is harmless. This is the throughput half of the Collection
-- panel's two-series trend; paired with GetCollectionDailyChanges.sql's confirmed-diff count.
SELECT
    date_trunc('day', cycle_at)    AS day
  , coalesce(sum(facts_sent), 0)   AS facts_sent
FROM
    agent_cycles
WHERE
    cycle_at >= date_trunc('day', now()) - make_interval(days => $1 - 1)
GROUP BY
    date_trunc('day', cycle_at)
ORDER BY
    day ASC
