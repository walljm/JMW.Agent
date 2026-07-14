SELECT
    online_multiplier
  , offline_ceiling_secs
FROM
    agent_liveness_settings
WHERE
    id = TRUE
