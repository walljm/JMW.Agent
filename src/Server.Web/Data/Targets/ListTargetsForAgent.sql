SELECT
    target_id
  , endpoint
  , collector_type
  , credential_id
  , label
  , enabled
FROM
    targets
WHERE
      agent_id = $1
  AND enabled = TRUE
ORDER BY
    created_at DESC
  , target_id  DESC
