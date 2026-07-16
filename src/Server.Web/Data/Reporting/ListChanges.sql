SELECT
    fh.id
  , fh.attribute_path
  , fh.key_values::text AS key_values
  , fh.kind
  , fh.value_str
  , fh.value_long
  , fh.value_double
  , fh.collected_at
  , s.hostname
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    facts_history          fh
    LEFT JOIN proj_systems s
    ON s.device = (fh.key_values ->>'Device')
WHERE
      (
          $1::timestamptz IS NULL
  OR fh.collected_at >= $1)
  AND (
          $2::text IS NULL
  OR fh.key_values->>'Device' = $2)
  AND (
          $3::timestamptz IS NULL
  OR fh.collected_at < $3
  OR (
    fh.collected_at = $3
  AND fh.id > $4))
ORDER BY
    fh.collected_at DESC
  , fh.id           ASC LIMIT $5
