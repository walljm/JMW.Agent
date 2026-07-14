-- The device's *current* facts (latest value per fact id, mirroring GetDeviceAllFacts.sql)
-- whose latest write came from the given collector — i.e. this collector's blast radius.
-- Feeds the Activity tab's "which facts does this failing collector own" drill-down (F4).
--
-- Must find the latest write per fact id FIRST (across every source), then filter to source —
-- filtering by source before dedup would wrongly include a fact this collector last wrote
-- historically but that a different, later collector has since superseded.
SELECT
    attribute_path
  , key_values
  , VALUE
  , collected_at
FROM
    (
        SELECT
            DISTINCT
        ON (h.id)
            h.attribute_path
          , h.key_values::text AS key_values
          , COALESCE (h.value_str, h.value_long::text, h.value_double::text) AS VALUE
          , h.collected_at
          , h.source_name
        FROM
            facts_history h
        WHERE
            h.key_values ->> 'Device' = $1::text
        ORDER BY
            h.id
          , h.collected_at DESC
    ) latest
WHERE
    latest.source_name = $2
