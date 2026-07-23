-- Clients relayed through a Google Wifi/OnHub mesh point (real, unobscured BSSID) — the one
-- signal a mesh-only agent can offer about satellite mesh points it has no direct access to
-- (see OnHubStations.cs, FactPaths.DiscoveredLinkMeshApBssid). Consumed by L2TopologyApi to
-- group clients under a synthetic mesh-point node when the relaying BSSID itself isn't already
-- a known device.
SELECT
    mac
  , obscured_mac
  , hostname
  , mesh_ap_bssid
FROM
    proj_discovered
WHERE
    mesh_ap_bssid IS NOT NULL
