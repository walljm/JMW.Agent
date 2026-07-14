-- proj_discovered rows carrying a vendor identity id (Hue bridge id / ONVIF hardware id).
-- Each id promotes to a fingerprint; the row's MAC (when known) is unioned in the resolve so
-- the id merges onto the same device an ARP/scanner observer sees by MAC.
SELECT
    d.mac
  , d.hue_bridge_id
  , d.onvif_hardware_id
FROM
    proj_discovered d
WHERE
     d.hue_bridge_id IS NOT NULL
  OR d.onvif_hardware_id IS NOT NULL
