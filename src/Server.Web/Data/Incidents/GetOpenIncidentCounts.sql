-- Dashboard "Needs Attention": one query, replaces GetPostureSummary.sql's hand-written
-- per-category COUNT...FILTER queries — a new incident_type shows up here with no new SQL.
SELECT
    incident_type
  , COALESCE(count(*), 0)                    AS open_count
  , COALESCE(count(DISTINCT entity_id), 0)   AS distinct_entities
FROM
    incidents
WHERE
    resolved_at IS NULL
GROUP BY
    incident_type
ORDER BY
    count(*) DESC
  , incident_type ASC
