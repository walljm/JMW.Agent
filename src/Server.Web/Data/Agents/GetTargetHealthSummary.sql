-- Per-target health for the Targets tab's inline cue, aggregated over the same rolling
-- window as the collector/scanner cues (since/until, open-ended when null). Unnests the
-- per-cycle device_scanners and services stat arrays — no schema change needed.
-- device_scanners entries carry `target` = the target's endpoint and `protocol` = its
-- collector_type; services entries carry `target` = label-or-endpoint (see
-- Agent.CollectServicesAsync) and `type` = collector_type. The (target, collector_type)
-- pair therefore matches one Targets-tab row; the page model looks the target up by
-- endpoint first, then label, to cover both conventions.
WITH combined AS (
    SELECT
        elem ->> 'target' AS target
      , elem ->> 'protocol' AS collector_type
      , (elem ->> 'duration_ms')::int AS duration_ms
      , elem ->> 'error' IS NOT NULL AS has_error
    FROM
        agent_cycles c
      , jsonb_array_elements(c.device_scanners) elem
    WHERE
        c.agent_id = $1
        AND ($2::timestamptz IS NULL OR c.cycle_at >= $2)
        AND ($3::timestamptz IS NULL OR c.cycle_at <= $3)
    UNION ALL
    SELECT
        elem ->> 'target' AS target
      , elem ->> 'type' AS collector_type
      , (elem ->> 'duration_ms')::int AS duration_ms
      , elem ->> 'error' IS NOT NULL AS has_error
    FROM
        agent_cycles c
      , jsonb_array_elements(c.services) elem
    WHERE
        c.agent_id = $1
        AND ($2::timestamptz IS NULL OR c.cycle_at >= $2)
        AND ($3::timestamptz IS NULL OR c.cycle_at <= $3)
)
SELECT
    target
  , collector_type
  , count(*)::int AS run_count
  , count(*) FILTER (WHERE has_error)::int AS error_count
  , percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms) AS median_duration_ms
FROM
    combined
WHERE
    target IS NOT NULL
GROUP BY
    target
  , collector_type
ORDER BY
    error_count DESC
  , target
