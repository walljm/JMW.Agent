UPDATE agents
SET
    status = 'approved'
WHERE
    agent_id = $1 RETURNING agent_id
