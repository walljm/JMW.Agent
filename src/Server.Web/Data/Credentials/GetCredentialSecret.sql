SELECT
    type
  , encrypted_blob
FROM
    credentials
WHERE
    credential_id = $1
