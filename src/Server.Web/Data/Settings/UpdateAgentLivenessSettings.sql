UPDATE agent_liveness_settings
SET
    online_multiplier = $1
  , offline_ceiling_secs = $2
  , updated_at = now()
WHERE
    id = TRUE RETURNING online_multiplier, offline_ceiling_secs
