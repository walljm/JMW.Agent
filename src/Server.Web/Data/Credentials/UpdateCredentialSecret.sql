UPDATE credentials
SET
    encrypted_blob = $2
  , updated_at     = now()
WHERE
    credential_id = $1 RETURNING credential_id
