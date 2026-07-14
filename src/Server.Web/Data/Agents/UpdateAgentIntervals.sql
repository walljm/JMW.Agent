UPDATE agents
SET
    heartbeat_interval_secs = $2
  , discovery_interval_secs = $3
  , inventory_interval_secs = $4
  , updated_at              = now()
WHERE
    agent_id = $1 RETURNING agent_id
