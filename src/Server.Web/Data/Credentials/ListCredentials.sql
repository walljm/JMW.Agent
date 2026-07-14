SELECT
    credential_id
  , name
  , type
  , created_at
  , updated_at
FROM
    credentials
WHERE
    (
        $1::text IS NULL
  OR type = $1)
  AND (
        $2::timestamptz IS NULL
  OR (
    created_at
   , credential_id)
   < (
    $2
   , $3))
ORDER BY
    created_at    DESC
  , credential_id DESC LIMIT $4
