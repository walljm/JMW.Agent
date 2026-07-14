-- Agent-level collection health for the Overview "Collection" tile: the most recent cycle that
-- actually ran a collector/scanner/service probe (regardless of age) plus cycle counts within
-- the rolling window since $2. Always returns exactly one row — every column is null/0 when the
-- agent has no cycles yet. Kept independent of the Activity date filter so the tile stays stable
-- while the operator filters the timeline below it.
--
-- "latest" excludes heartbeat-only cycles (collectors/scanners/device_scanners/services all
-- empty — the agent ticks roughly every heartbeat interval, but discovery/inventory only run on
-- their own longer intervals, so most ticks do nothing). Picking the literal last row by cycle_at
-- almost always landed on one of these no-op ticks, showing "0 facts · 0 err" on the tile even
-- when a real cycle with hundreds of facts ran minutes earlier — exactly what the Recent
-- Activity cycle history (unfiltered, still shows every cycle) and its graph make obvious is
-- wrong the moment you look past the tile.
SELECT
    -- CASE-wrapped (not a bare column reference) so Npgsql's schema introspection reports
    -- these as nullable — a direct `latest.cycle_at` passthrough gets reported NOT NULL
    -- (reflecting agent_cycles' own column constraint), even though the LEFT JOIN LATERAL
    -- can legitimately produce an all-NULL `latest` row for an agent with zero cycles.
    CASE WHEN latest.cycle_at IS NOT NULL THEN latest.cycle_at END       AS last_cycle_at
  , CASE WHEN latest.facts_sent IS NOT NULL THEN latest.facts_sent END   AS last_facts
  , CASE WHEN latest.error_count IS NOT NULL THEN latest.error_count END AS last_errors
  , CASE WHEN latest.duration_ms IS NOT NULL THEN latest.duration_ms END AS last_duration_ms
  , coalesce(win.total, 0)    AS window_total
  , coalesce(win.errored, 0)  AS window_errored
FROM
    (
        SELECT
            count(*)::int                                   AS total
          , count(*) FILTER (WHERE error_count > 0)::int    AS errored
        FROM
            agent_cycles
        WHERE
            agent_id = $1
            AND cycle_at >= $2
    ) win
LEFT JOIN LATERAL (
    SELECT
        cycle_at
      , facts_sent
      , error_count
      , duration_ms
    FROM
        agent_cycles
    WHERE
        agent_id = $1
        AND (
              jsonb_array_length(collectors) > 0
           OR jsonb_array_length(scanners) > 0
           OR jsonb_array_length(device_scanners) > 0
           OR jsonb_array_length(services) > 0
            )
    ORDER BY
        cycle_at DESC
    LIMIT 1
) latest ON true
