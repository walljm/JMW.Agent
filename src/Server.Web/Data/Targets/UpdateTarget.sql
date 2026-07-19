UPDATE targets
SET
    endpoint       = $2
  , collector_type = $3
  , credential_id  = $4
  , label          = $5
  , endpoint_kind  = $6
  , updated_at     = now()
WHERE
    target_id = $1 RETURNING target_id
