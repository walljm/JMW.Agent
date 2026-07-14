-- D7 consolidation: "NOT EXISTS (SELECT 1 FROM device_aliases da WHERE da.alias_device_id =
-- d.device_id)" — the predicate that hides devices merged away as an alias of another device —
-- was copy-pasted across 9 call sites (ListDevices, GetActivitySummary, GetNetworkSummary, the
-- three GetCompositionBy* reports, GetNewDevices, GetNotSeenDevices, and HostsApi's inline CTE).
-- A query that forgets it silently double-counts merged devices. One view, one place to get it
-- right; callers select FROM live_devices instead of devices and drop the predicate.
CREATE
OR REPLACE VIEW live_devices AS
SELECT d.*
FROM devices d
WHERE NOT EXISTS (
    SELECT 1 FROM device_aliases da WHERE da.alias_device_id = d.device_id
);
