SELECT
    table_name
  , time_column
  , stale_after
FROM
    retention_policies
WHERE
      enabled = TRUE
  AND stale_after IS NOT NULL
ORDER BY
    category
  , table_name
