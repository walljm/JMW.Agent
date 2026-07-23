-- Liveness derived by agent_liveness() (see migration 0056_agent_liveness_settings.sql) —
-- the single definition shared by GetAgentHealthSummary.sql / GetAgentHealthList.sql. The
-- function is called inline (not via a CTE) so every other column keeps its base-table
-- nullability in the reported schema — CTE outputs never resolve to base columns.
SELECT
    agent_id
  , hostname
  , status
  , last_heartbeat
  , zone
  , version
  , passive_discovery_mode
  , os
  , arch
  , ip_address
  , device_id
  , created_at
  , agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
FROM
    agents
WHERE
      (
          $1::text IS NULL
     OR status = $1
          )
  AND (
          $2::text IS NULL
     OR zone = $2
          )
  AND (
          $3::text IS NULL
     OR version = $3
          )
  AND (
          $4::text IS NULL
     OR agent_liveness(last_heartbeat, heartbeat_interval_secs) = $4
          )
  AND (
          $5::text IS NULL
     OR hostname ILIKE '%' || $5 || '%'
     OR ip_address ILIKE '%' || $5 || '%'
          )
  AND (
          $8::text IS NULL
     OR (__SORT_KEY__, created_at, agent_id::text) __CMP__ ($6::timestamptz, $7::timestamptz, $8)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , created_at __DIR__
  , agent_id::text __DIR__
LIMIT $9
