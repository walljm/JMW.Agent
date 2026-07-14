SELECT
    table_name
  , category
  , time_column
  , stale_after
  , enabled
  , notes
FROM
    retention_policies
ORDER BY
    category
  , table_name
