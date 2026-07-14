UPDATE agents
SET
    device_id = $2
WHERE
      agent_id = $1
  AND device_id IS NULL RETURNING agent_id
