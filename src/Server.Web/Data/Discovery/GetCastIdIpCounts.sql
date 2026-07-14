-- Distinct neighbor-IP count per Cast device id, over ALL discovered rows. Counted
-- across the full table (not just obscured-MAC rows) because a cast device's current
-- row may carry the cast id with no obscured_mac (networkState-only). A cast id at
-- >1 IP is a stale/roamed advertisement → the materializer resolves it by cast id
-- alone so its name never binds to the wrong hardware's MAC.
SELECT
    cast_id
  , count(DISTINCT discovered) AS ip_count
FROM
    proj_discovered
WHERE
    cast_id IS NOT NULL
GROUP BY
    cast_id
