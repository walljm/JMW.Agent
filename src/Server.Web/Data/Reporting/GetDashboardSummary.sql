-- PERF-005: two FILTER aggregates replace five separate COUNT(*) subqueries.
-- devices scanned once, agents scanned once.
WITH device_counts AS (
    -- visible_devices: excludes merged-away aliases AND devices hidden by the liveness window,
    -- so the headline tiles count only the live inventory the Devices list shows.
    SELECT
        count(*) AS total_devices
      , count(*) FILTER (WHERE management_status = 'managed')    AS managed_devices
      , count(*) FILTER (WHERE management_status = 'discovered') AS discovered_devices
    FROM
        visible_devices
    )
   , agent_counts  AS (
    SELECT
        count(*) AS total_agents
      , count(*) FILTER (WHERE status = 'approved')      AS approved_agents
      , count(*) FILTER (WHERE status = 'pending')       AS pending_agents
    FROM
        agents
    )
SELECT
    d.total_devices
  , d.managed_devices
  , d.discovered_devices
  , a.total_agents
  , a.approved_agents
  , a.pending_agents
FROM
    device_counts d
  , agent_counts  a
