-- 0015 networks: network identity + device-network association
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS networks (
    id TEXT PRIMARY KEY,                        -- server-generated UUID
    name TEXT NOT NULL DEFAULT '',              -- human label, e.g. "Home LAN"
    gateway_mac TEXT NOT NULL,                  -- stable identity anchor (lowercase colon-sep)
    cidr TEXT,                                  -- informational, auto-updated from agent reports
    ssid TEXT,                                  -- Wi-Fi SSID if known, NULL for wired
    status TEXT NOT NULL DEFAULT 'discovered',  -- discovered | monitored | ignored
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_networks_gwmac ON networks(gateway_mac);
CREATE INDEX IF NOT EXISTS idx_networks_status ON networks(status);

CREATE TABLE IF NOT EXISTS device_networks (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    network_id TEXT NOT NULL REFERENCES networks(id) ON DELETE CASCADE,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    PRIMARY KEY (device_id, network_id)
);
CREATE INDEX IF NOT EXISTS idx_device_networks_net ON device_networks(network_id);
