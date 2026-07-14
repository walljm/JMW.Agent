DELETE
FROM
    credentials
WHERE
      credential_id = $1
  AND NOT exists
          (
              SELECT
                  1
              FROM
                  targets
              WHERE
                  credential_id = $1
              )
    RETURNING credential_id
