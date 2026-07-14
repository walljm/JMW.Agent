SELECT
    target_id
  , agent_id
  , endpoint
  , collector_type
  , credential_id
  , label
  , enabled
  , created_at
  , updated_at
FROM
    targets
WHERE
      (
          $1::uuid IS NULL
  OR agent_id = $1)
  AND (
          $2::timestamptz IS NULL
  OR (
    created_at
   , target_id)
   < (
    $2
   , $3))
ORDER BY
    created_at DESC
  , target_id  DESC LIMIT $4
