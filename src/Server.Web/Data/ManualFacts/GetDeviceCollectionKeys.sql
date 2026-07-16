-- Distinct observed keys of a device's ($1) child-collection dimension ($2, e.g. 'Interface'),
-- backing the child-key combo box (REQ-010). Works for any dimension — catalog or arbitrary — with
-- no per-dimension projection knowledge. Unmatched typed keys are still accepted downstream; this
-- only supplies suggestions from what has actually been observed.
SELECT DISTINCT
    h.key_values ->> $2 AS collection_key
FROM
    facts_history h
WHERE
      h.key_values ->> 'Device' = $1::text
  AND h.key_values ? $2
  AND h.key_values ->> $2 IS NOT NULL
ORDER BY
    collection_key
