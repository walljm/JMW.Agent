-- Status-primary twin of ListAgentsByCreatedAt.sql — a separate command because the cursor's
-- first element is text here but timestamptz there, and a bind parameter can only have one
-- type per command. created_at stays the (typed) tiebreaker in both.
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
     OR (__SORT_KEY__, created_at, agent_id::text) __CMP__ ($6::text, $7::timestamptz, $8)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , created_at __DIR__
  , agent_id::text __DIR__
LIMIT $9
