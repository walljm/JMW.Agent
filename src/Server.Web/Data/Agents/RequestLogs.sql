UPDATE agents
SET
    logs_requested_at = now()
  , logs_requested_lines = $2
  , logs_requested_before = $3
WHERE
    agent_id = $1 RETURNING agent_id, logs_requested_at
