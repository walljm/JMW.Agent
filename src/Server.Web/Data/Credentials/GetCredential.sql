SELECT
    credential_id
  , name
  , type
  , created_at
  , updated_at
FROM
    credentials
WHERE
    credential_id = $1
