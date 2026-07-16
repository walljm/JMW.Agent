-- Every operator-authored (FactSource.ManualEntry, source=2) fact for one device ($1) — latest
-- value per fact id — with any path-level label metadata. Served by facts_history_id_time_idx via
-- the Device[<id>] id prefix; source=2 is a cheap post-filter over the device's own small fact set
-- (architecture §7.1). key_values minus 'Device' is the device-independent metadata key.
SELECT DISTINCT ON (h.id)
    h.attribute_path
  , h.key_values::text AS key_values
  , COALESCE(h.value_str, h.value_long::text, h.value_double::text) AS value
  , m.label
  , h.source_name
  , h.collected_at
FROM
    facts_history h
    LEFT JOIN fact_path_metadata m
        ON  m.attribute_path = h.attribute_path
        AND m.key_values = (h.key_values - 'Device')
WHERE
      h.key_values ->> 'Device' = $1::text
  AND h.source = 2
ORDER BY
    h.id
  , h.collected_at DESC
