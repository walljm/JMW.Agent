UPDATE retention_policies
SET
    stale_after = $2
  , enabled     = $3
WHERE
    table_name = $1 RETURNING COALESCE(TABLE_NAME, NULL) AS TABLE_NAME
