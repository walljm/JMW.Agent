-- Collection rollup over the latest REAL cycle per agent: total facts sent, agents whose latest
-- real cycle had errors, average cycle duration, and how many agents have a real cycle on record.
--
-- "Real" excludes heartbeat-only cycles (collectors/scanners/device_scanners/services all empty —
-- the agent ticks roughly every heartbeat interval, but discovery/inventory only run on their own
-- longer intervals, so most ticks do nothing). Picking the literal last row by cycle_at almost
-- always landed on one of these no-op ticks, showing "0 facts sent" fleet-wide even when real
-- cycles with hundreds of facts ran minutes earlier. Same fix as GetAgentCollectionSummary.sql's
-- per-agent tile, applied fleet-wide.
WITH latest AS (
    SELECT DISTINCT
ON (agent_id)
    agent_id
    , duration_ms
    , facts_sent
    , error_count
FROM
    agent_cycles
WHERE
    jsonb_array_length(collectors) > 0
    OR jsonb_array_length(scanners) > 0
    OR jsonb_array_length(device_scanners) > 0
    OR jsonb_array_length(services) > 0
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
