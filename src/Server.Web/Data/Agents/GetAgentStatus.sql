SELECT
    -- COALESCE reports the column as nullable to match the string? result tuple
    -- (FirstOrDefault yields a null Status as the "agent not found" sentinel).
    COALESCE(status, NULL) AS status
FROM
    agents
WHERE
    agent_id = $1
