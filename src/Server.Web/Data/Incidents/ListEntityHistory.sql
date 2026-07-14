-- Device Detail History tab: full incident+event timeline for one entity, most-recent-first.
SELECT
    *
FROM
    (
        SELECT
            CASE WHEN resolved_at IS NULL THEN 'open' ELSE 'resolved' END AS kind
          , incident_type                                                AS type_name
          , detail
          , coalesce(resolved_at, opened_at)                             AS at
          , CASE WHEN resolved_at IS NOT NULL THEN resolved_at - opened_at ELSE NULL END AS duration
          , resolution
        FROM
            incidents
        WHERE
            entity_kind = $1 AND entity_id = $2
        UNION ALL
        SELECT
            'event'          AS kind
          , event_type       AS type_name
          , detail
          , occurred_at      AS at
          , NULL::interval   AS duration
          , NULL::text       AS resolution
        FROM
            change_events
        WHERE
            entity_kind = $1 AND entity_id = $2
    ) merged
ORDER BY
    at DESC
LIMIT $3
