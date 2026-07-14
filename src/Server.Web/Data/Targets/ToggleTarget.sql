UPDATE targets
SET
    enabled    = NOT enabled
  , updated_at = now()
WHERE
    target_id = $1 RETURNING target_id, enabled
