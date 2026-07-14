-- Liveness derived by agent_liveness() (see migration 0056_agent_liveness_settings.sql) — the
-- same definition GetAgentHealthSummary.sql/GetAgentHealthList.sql/AgentsApi already use.
SELECT
    agent_id
  , agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
FROM
    agents
