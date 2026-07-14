-- Latest value of every fact keyed to this service (DISTINCT ON collapses the append log).
-- Feeds the service-detail fact views (Home Assistant, CA DNS names, DNS top-N).
SELECT DISTINCT
ON (h.id)
    h.attribute_path
    , h.key_values::text AS key_values
    , COALESCE (h.value_str, h.value_long::text, h.value_double::text) AS VALUE
FROM
    facts_history h
WHERE
    h.key_values ->> 'Service' = $1
ORDER BY
    h.id
  , h.collected_at DESC
