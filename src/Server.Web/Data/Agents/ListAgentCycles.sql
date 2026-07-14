-- $6 (collection_only): excludes bare heartbeat ticks — the collection loop ticks at the
-- fastest of the heartbeat/discovery/inventory cadences and posts a cycle every tick even when
-- neither discovery nor inventory was due, so on agents with a short heartbeat interval most
-- rows can otherwise be empty noise. A tick counts as a real collection run when any collector,
-- scanner, device-scanner, or service ran, even if that run itself found nothing.
SELECT
    cycle_id
  , cycle_at
  , duration_ms
  , facts_sent
  , error_count
  , collectors
  , scanners
  , device_scanners
  , services
FROM
    agent_cycles
WHERE
    agent_id = $1
    AND ($2::timestamptz IS NULL OR cycle_at >= $2)
    AND ($3::timestamptz IS NULL OR cycle_at <= $3)
    AND (NOT $4 OR error_count > 0)
    AND (
        NOT $6
        OR jsonb_array_length(collectors) > 0
        OR jsonb_array_length(scanners) > 0
        OR jsonb_array_length(device_scanners) > 0
        OR jsonb_array_length(services) > 0
    )
ORDER BY
    cycle_at DESC LIMIT $5
