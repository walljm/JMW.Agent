-- 0014 DHCP overlay columns
--
-- Populated by the server's terrain poller (AdGuard/Technitium/Pi-hole)
-- and by the agent's local dhcpLookup. Lets the UI surface "pinned in
-- DHCP" devices and show when DHCP last confirmed a device's lease,
-- independent of agent ARP scans.
ALTER TABLE devices ADD COLUMN static_lease INTEGER NOT NULL DEFAULT 0;
ALTER TABLE devices ADD COLUMN dhcp_seen_at TEXT;
CREATE INDEX IF NOT EXISTS idx_devices_static_lease ON devices(static_lease);
