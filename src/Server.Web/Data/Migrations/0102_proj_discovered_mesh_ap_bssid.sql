-- The physical Google Wifi/OnHub mesh point (real, unobscured BSSID) currently relaying a
-- discovered client (Device[].Discovered[].Link.MeshApBssid — see OnHubStations.cs). A discrete
-- identity value like hostname/vendor on this same table, not a metric, so it belongs on
-- proj_discovered rather than metrics_raw. Read by L2TopologyApi to group clients under their
-- relaying mesh point on the L2 topology graph. Fill-only, nullable.
ALTER TABLE proj_discovered
    ADD COLUMN IF NOT EXISTS mesh_ap_bssid TEXT;
