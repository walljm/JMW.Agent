UPDATE agents
SET
    collectors_config = collectors_config || $2::jsonb
  , updated_at        = now()
WHERE
    agent_id = $1
    RETURNING agent_id
