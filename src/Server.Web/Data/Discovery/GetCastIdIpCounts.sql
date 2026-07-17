-- Distinct neighbor-IP count per Cast device id, over ALL discovered rows. Counted
-- across the full table (not just obscured-MAC rows) because a cast device's current
-- row may carry the cast id with no obscured_mac (networkState-only). A cast id at
-- >1 IP is a stale/roamed advertisement → the materializer resolves it by cast id
-- alone so its name never binds to the wrong hardware's MAC.
-- Reads materialization_facts (docs/plans/architecture-identity-facts.md §5 Phase 2a) —
-- CastId moved off proj_discovered; entity_key is the neighbor IP.
SELECT
    value AS cast_id
  , count(DISTINCT entity_key) AS ip_count
FROM
    materialization_facts
WHERE
    attribute_path = 'Device[].Discovered[].CastId'
GROUP BY
    value
