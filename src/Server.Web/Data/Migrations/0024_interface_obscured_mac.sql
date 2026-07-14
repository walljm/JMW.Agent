-- Add obscured_mac column to proj_interfaces.
-- Google Wifi reports the AP's own interface MACs obscured (real OUI, device bytes
-- masked with '*', e.g. "703acb70d06*"). The agent records that value here as a raw
-- kept fact (FactPaths.InterfaceObscuredMAC) — it is never a device fingerprint. The
-- DiscoveryMaterializer reconstructs it against the known-MAC set for the interface's
-- IP and, on a unique OUI-corroborated match, populates the row's `mac_address`.
ALTER TABLE jmwdiscovery.proj_interfaces
    ADD COLUMN if NOT EXISTS obscured_mac text;
