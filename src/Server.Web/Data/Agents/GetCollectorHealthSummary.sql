-- Per-collector/scanner/device-target/service-target health aggregated across the same
-- visible window as the Activity tab (since/until, open-ended when null) — unnests the
-- existing per-cycle stat arrays via jsonb_array_elements, no schema change needed.
-- Collectors and scanners report a `name` (their own runtime slug, e.g. "arp" — not their
-- class name); device_scanners and services have no single-instance identity worth grouping
-- by (device_scanners are per-target, keyed by IP; services are per-target, keyed by label),
-- so they're grouped by the dimension that mirrors "which collector ran": device_scanners by
-- `protocol` (e.g. "ssh", "snmp"), services by `type` (e.g. "http", "tcp"). All four are
-- unioned into one combined view, tagged with a `kind` discriminator — a local collector and
-- a network scanner can report the same slug (ArpCollector and ArpScanner both report "arp"),
-- so `kind` keeps their stats from being merged into one row.
WITH combined AS (
    SELECT
        elem ->> 'name' AS name
      , 'collector' AS kind
      , (elem ->> 'duration_ms')::int AS duration_ms
      , elem ->> 'error' IS NOT NULL AS has_error
    FROM
        agent_cycles c
      , jsonb_array_elements(c.collectors) elem
    WHERE
        c.agent_id = $1
        AND ($2::timestamptz IS NULL OR c.cycle_at >= $2)
        AND ($3::timestamptz IS NULL OR c.cycle_at <= $3)
    UNION ALL
    SELECT
        elem ->> 'name' AS name
      , 'scanner' AS kind
      , (elem ->> 'duration_ms')::int AS duration_ms
      , elem ->> 'error' IS NOT NULL AS has_error
    FROM
        agent_cycles c
      , jsonb_array_elements(c.scanners) elem
    WHERE
        c.agent_id = $1
        AND ($2::timestamptz IS NULL OR c.cycle_at >= $2)
        AND ($3::timestamptz IS NULL OR c.cycle_at <= $3)
    UNION ALL
    SELECT
        elem ->> 'protocol' AS name
      , 'device-scanner' AS kind
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
        elem ->> 'type' AS name
      , 'service' AS kind
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
    name
  , kind
  , count(*)::int AS run_count
  , count(*) FILTER (WHERE has_error)::int AS error_count
  , percentile_cont(0.5) WITHIN GROUP (ORDER BY duration_ms) AS median_duration_ms
FROM
    combined
WHERE
    name IS NOT NULL
GROUP BY
    name
  , kind
ORDER BY
    error_count DESC
  , name
