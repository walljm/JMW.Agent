-- Confirmed fact changes per day, summed across the whole fleet, over the last $1 days (inclusive
-- of today). Counts facts_history rows — inserted only when a value differs from the server's
-- last known value (see FactRepository's dedup CTE), so this is the "did anything genuinely
-- change" half of the Collection panel's two-series trend; paired with
-- GetCollectionDailyFactsSent.sql's raw send volume. Index-backed by
-- facts_history(collected_at DESC). Days with no changes are absent from the result; the caller
-- fills gaps with zero.
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
