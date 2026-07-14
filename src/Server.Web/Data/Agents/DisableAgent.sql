UPDATE agents
SET
    status = 'disabled'
WHERE
    agent_id = $1 RETURNING agent_id
