-- Add obscured_mac column to proj_discovered.
-- Google Wifi reports every MAC with its last hex nibble masked ('*', e.g.
-- "00e0bf1fc40*"). The agent records that value here as a raw kept fact
-- (FactPaths.DiscoveredObscuredMAC) — it is never a device fingerprint. The
-- DiscoveryMaterializer reconstructs it against the known-MAC set and, on a
-- unique match, populates the row's `mac` column (which then identifies/merges
-- the device through the normal discovered-MAC path).
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN if NOT EXISTS obscured_mac text;
