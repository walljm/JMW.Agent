-- Device-scoped fact paths flagged as report columns (fact_path_metadata.show_in_reports),
-- with display labels. key_values = '{}' restricts to device-scoped paths — a child-collection
-- fact (non-empty key_values) has no single per-device value and can never be a device column.
SELECT
    attribute_path
  , label
FROM
    fact_path_metadata
WHERE
      show_in_reports
  AND key_values = '{}'::jsonb
ORDER BY
    attribute_path
