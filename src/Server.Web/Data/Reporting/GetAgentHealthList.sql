-- Agents for the health panel table, worst liveness first (offline → stale → online), then
-- oldest heartbeat first, capped at $1. Liveness derived by agent_liveness() (see migration
-- 0056_agent_liveness_settings.sql) — the single definition shared by GetAgentHealthSummary.sql
-- and AgentsApi.QueryAsync.
SELECT
    agent_id
  , hostname
  , status
  , last_heartbeat
  , ZONE
  , version
  , passive_discovery_mode
  , liveness
FROM
    (
    SELECT
    agent_id
  , hostname
  , status
  , last_heartbeat
  , ZONE
  , version
  , passive_discovery_mode
  , agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
    FROM
    agents
    ) t
ORDER BY
    CASE liveness WHEN 'offline' THEN 0 WHEN 'stale' THEN 1 ELSE 2
END ASC
  , last_heartbeat ASC NULLS FIRST
  , hostname       ASC LIMIT $1
