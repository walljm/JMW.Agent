-- Agent health rollup: approval counts + heartbeat-derived liveness.
-- Liveness derived by agent_liveness() (see migration 0056_agent_liveness_settings.sql) — the
-- single definition shared by GetAgentHealthList.sql and AgentsApi.QueryAsync, configurable via
-- the agent_liveness_settings table (Settings page).
SELECT
    count(*) AS total_agents
  , count(*) FILTER (WHERE status = 'approved')    AS approved_agents
  , count(*) FILTER (WHERE status = 'pending')     AS pending_agents
  , count(*) FILTER (WHERE liveness = 'online')    AS online_agents
  , count(*) FILTER (WHERE liveness = 'stale')     AS stale_agents
  , count(*) FILTER (WHERE liveness = 'offline')   AS offline_agents
FROM
    (
        SELECT
            status
          , agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
        FROM
            agents
        ) t
