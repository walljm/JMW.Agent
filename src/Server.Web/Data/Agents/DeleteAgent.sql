DELETE
FROM
    agents
WHERE
    agent_id = $1 RETURNING agent_id
