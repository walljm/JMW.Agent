-- Collection rollup over the latest cycle per agent: total facts sent, agents whose latest
-- cycle had errors, average cycle duration, and how many agents have reported a cycle.
WITH latest AS (
    SELECT DISTINCT
ON (agent_id)
    agent_id
    , duration_ms
    , facts_sent
    , error_count
FROM
    agent_cycles
ORDER BY
    agent_id
  , cycle_at DESC
    )
SELECT
    coalesce(sum(facts_sent), 0) AS facts_sent_total
  , count(*)                     FILTER (WHERE error_count > 0)    AS agents_with_errors
  , coalesce(round(avg(duration_ms)), 0)::bigint AS avg_duration_ms
  , count(*) AS agents_reporting
FROM
    latest
