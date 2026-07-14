UPDATE agents
SET
    clear_trackers_requested_at = now()
WHERE
    agent_id = $1 RETURNING agent_id
