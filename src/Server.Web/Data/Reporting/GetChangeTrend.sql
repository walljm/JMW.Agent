-- Fact-change counts per day over the last $1 days (inclusive of today), for the trend
-- sparkline. Index-backed by facts_history(collected_at DESC). Days with no changes are
-- absent from the result; the caller fills gaps with zero.
SELECT
    date_trunc('day', collected_at) AS day
  , count(*)                        AS count
FROM
    facts_history
WHERE
    collected_at >= date_trunc('day', now()) - make_interval(days => $1 - 1)
GROUP BY
    date_trunc('day', collected_at)
ORDER BY
    day ASC
