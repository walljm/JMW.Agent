SELECT
    agent_id
  , status
FROM
    agents
WHERE
    api_key_hash = $1
