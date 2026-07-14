UPDATE credentials
SET
    name       = $2
  , type       = $3
  , updated_at = now()
WHERE
    credential_id = $1 RETURNING credential_id
