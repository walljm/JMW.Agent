UPDATE agents
SET
    zone = $2
WHERE
    agent_id = $1 RETURNING agent_id
