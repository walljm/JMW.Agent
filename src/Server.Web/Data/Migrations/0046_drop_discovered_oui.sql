-- Drop proj_discovered.oui. The agent no longer emits Device[].Discovered[].Oui
-- (added in 0023): OUI is a NIC-block identifier, NOT a device vendor. The only
-- reader (GetDeviceSightings.sql) now derives the NIC vendor from the MAC at read
-- time via oui_vendor(mac), the same way ListArp/ListDhcpLeases/HostsApi do. The
-- projection mapping and the FactPaths.DiscoveredOui constant were removed with it,
-- so no query reads this column (verified via grep audit). The vendor column stays
-- (populated by onvif/upnp manufacturer, a real device vendor — oui != vendor).
ALTER TABLE jmwdiscovery.proj_discovered
DROP
COLUMN IF EXISTS oui CASCADE;
