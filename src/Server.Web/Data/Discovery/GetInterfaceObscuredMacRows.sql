-- Interface rows carrying an obscured MAC, for the AP's own interfaces. Emitted on
-- EVERY pass (NOT gated on mac_address), so the reconstructed real MAC is re-fed into
-- the device resolve each cycle: a scanner/ARP device that first records the same real
-- MAC in a LATER batch still merges onto the AP. `mac_address` is the already-
-- reconstructed real MAC (null until reconstructed); an IPv4 is required as the join
-- key for the first reconstruction. `interface` is the row key within the device.
SELECT
    device
  , interface
  , ipv4 AS ip
  , obscured_mac
  , mac_address
FROM
    proj_interfaces
WHERE
      obscured_mac IS NOT NULL
  AND ipv4 IS NOT NULL
