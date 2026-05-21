-- 0002 alerts + discovery + retention
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS alert_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    metric TEXT NOT NULL,                 -- cpu_pct | mem_pct | disk_pct | offline_minutes
    op TEXT NOT NULL,                     -- gt | lt
    threshold REAL NOT NULL,
    duration_seconds INTEGER NOT NULL DEFAULT 60,
    target_kind TEXT NOT NULL DEFAULT 'agent', -- agent | group | all
    target_id TEXT,
    severity TEXT NOT NULL DEFAULT 'warning',
    channel_id INTEGER,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS alert_firings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id INTEGER NOT NULL REFERENCES alert_rules(id) ON DELETE CASCADE,
    agent_id TEXT,
    started_at TEXT NOT NULL,
    resolved_at TEXT,
    last_value REAL,
    summary TEXT,
    notified INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_firings_open ON alert_firings(rule_id, agent_id, resolved_at);

CREATE TABLE IF NOT EXISTS notification_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,                   -- email | webhook
    config_json TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS devices (
    id TEXT PRIMARY KEY,                  -- canonical id, e.g. mac or hostname/ip hash
    mac TEXT,
    ip TEXT,
    hostname TEXT,
    vendor TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_by_agent TEXT,
    kind TEXT,                            -- server | iot | printer | router | unknown
    notes TEXT
);
CREATE INDEX IF NOT EXISTS idx_devices_mac ON devices(mac);
CREATE INDEX IF NOT EXISTS idx_devices_ip ON devices(ip);

CREATE TABLE IF NOT EXISTS device_sightings (
    device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    seen_at TEXT NOT NULL,
    seen_by_agent TEXT,
    ip TEXT,
    mac TEXT,
    method TEXT,                          -- arp | mdns | ping
    PRIMARY KEY (device_id, seen_at)
);
