-- Recent Activity (fleet-wide): incidents + change_events merged into one narrative feed,
-- most-recent-first. Each incident appears as ONE row at its most recent transition (open, or
-- resolved with duration) — not a row per transition. Both inner branches are pre-limited via
-- their own recency index before the merge, so the outer sort never scans the full tables.
-- Hostname/agent-name resolution happens AFTER limiting, over at most $1 rows.
WITH merged AS (
    SELECT
        *
    FROM
        (
            (
                SELECT
                    CASE WHEN resolved_at IS NULL THEN 'open' ELSE 'resolved' END AS kind
                  , incident_type                                                AS type_name
                  , entity_kind
                  , entity_id
                  , detail
                  , coalesce(resolved_at, opened_at)                             AS at
                  , CASE WHEN resolved_at IS NOT NULL THEN resolved_at - opened_at ELSE NULL END AS duration
                  , resolution
                FROM
                    incidents
                ORDER BY
                    coalesce(resolved_at, opened_at) DESC
                LIMIT $1
            )
            UNION ALL
            (
                SELECT
                    'event'          AS kind
                  , event_type       AS type_name
                  , entity_kind
                  , entity_id
                  , detail
                  , occurred_at      AS at
                  , NULL::interval   AS duration
                  , NULL::text       AS resolution
                FROM
                    change_events
                ORDER BY
                    occurred_at DESC
                LIMIT $1
            )
        ) branches
    ORDER BY
        at DESC
    LIMIT $1
)
SELECT
    merged.kind
  , merged.type_name
  , merged.entity_kind
  , merged.entity_id
  , merged.detail
  , merged.at
  , merged.duration
  , merged.resolution
  , coalesce(ps.hostname, ag.hostname) AS entity_name
FROM
    merged
    LEFT JOIN proj_systems ps ON merged.entity_kind = 'device' AND ps.device = merged.entity_id
    LEFT JOIN agents ag ON merged.entity_kind = 'agent' AND ag.agent_id::text = merged.entity_id
ORDER BY
    merged.at DESC
