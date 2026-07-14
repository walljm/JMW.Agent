-- Centralizes the "online/stale/offline" liveness thresholds that were previously
-- hardcoded identically in GetAgentHealthList.sql, GetAgentHealthSummary.sql, and
-- AgentsApi.QueryAsync's inline CTE — three copies of the same magic numbers (3x
-- heartbeat interval for online, 1 hour ceiling for offline) that could silently
-- drift from each other. One settings row + one SQL function is now the single
-- definition; all three call sites (plus AgentDetail's liveness display) read it.
--
-- Single-row table: id is always TRUE, enforced by the primary key + check.
CREATE TABLE agent_liveness_settings (
    id boolean NOT NULL PRIMARY KEY DEFAULT TRUE CHECK (id),
    online_multiplier int NOT NULL DEFAULT 3,
    offline_ceiling_secs int NOT NULL DEFAULT 3600,
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO agent_liveness_settings (id) VALUES (TRUE);

CREATE OR REPLACE FUNCTION agent_liveness(last_heartbeat timestamptz, heartbeat_interval_secs int)
RETURNS text
LANGUAGE sql
STABLE
AS $$
    SELECT
        CASE
            WHEN last_heartbeat IS NULL THEN 'offline'
            WHEN now() - last_heartbeat > make_interval(secs => s.offline_ceiling_secs) THEN 'offline'
            WHEN now() - last_heartbeat <= make_interval(secs => heartbeat_interval_secs * s.online_multiplier)
                THEN 'online'
            ELSE 'stale'
        END
    FROM
        agent_liveness_settings s
$$;
