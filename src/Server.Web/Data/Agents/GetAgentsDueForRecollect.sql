-- Approved agents due for a forced full re-collect (the safety net that refills delta-suppressed
-- 'steady' current-state before retention could prune it into a permanent hole). Due = trackers
-- last requested-cleared more than the cadence ago, or never.
--
-- Cadence = a QUARTER of the shortest enabled 'steady' retention window, so it always sits
-- comfortably below that window (a present device's steady rows are refilled ~4x before they could
-- age out) and auto-follows if the retention is retuned — no separate constant to keep in sync.
--
-- Oldest-first + LIMIT ($1) staggers the fleet across sweeps rather than clearing everyone at once.
WITH cadence AS (
    SELECT
        min(stale_after) / 4 AS window
    FROM
        retention_policies
    WHERE
        category = 'steady'
      AND enabled
    )
SELECT
    a.agent_id
FROM
    agents a
  , cadence c
WHERE
      a.status = 'approved'
  AND c.window IS NOT NULL
  AND (a.clear_trackers_requested_at IS NULL OR a.clear_trackers_requested_at < now() - c.window)
ORDER BY
    a.clear_trackers_requested_at ASC NULLS FIRST
    LIMIT $1
