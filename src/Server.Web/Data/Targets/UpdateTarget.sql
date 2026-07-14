UPDATE targets
SET
    endpoint       = $2
  , collector_type = $3
  , credential_id  = $4
  , label          = $5
  , updated_at     = now()
WHERE
    target_id = $1 RETURNING target_id
