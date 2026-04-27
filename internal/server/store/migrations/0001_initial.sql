-- 0001 initial schema
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    last_used_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_expires ON sessions(expires_at);

CREATE TABLE IF NOT EXISTS config (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    description TEXT
);

CREATE TABLE IF NOT EXISTS agents (
    id TEXT PRIMARY KEY,
    hostname TEXT NOT NULL,
    os TEXT NOT NULL,
    arch TEXT NOT NULL,
    version TEXT,
    status TEXT NOT NULL,                   -- pending|approved|deregistered
    approved_at TEXT,
    approved_by TEXT,
    registered_at TEXT NOT NULL,
    last_heartbeat_at TEXT,
    enabled_subsystems TEXT NOT NULL DEFAULT '[]',
    notes TEXT,
    group_id INTEGER REFERENCES groups(id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS idx_agents_status ON agents(status);
CREATE INDEX IF NOT EXISTS idx_agents_last_heartbeat ON agents(last_heartbeat_at);

CREATE TABLE IF NOT EXISTS metric_snapshots (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    ts TEXT NOT NULL,
    cpu_pct REAL,
    mem_used_bytes INTEGER,
    mem_total_bytes INTEGER,
    load_1 REAL,
    load_5 REAL,
    load_15 REAL,
    uptime_seconds INTEGER,
    PRIMARY KEY (agent_id, ts)
);
CREATE INDEX IF NOT EXISTS idx_metrics_ts ON metric_snapshots(ts);

CREATE TABLE IF NOT EXISTS disk_snapshots (
    agent_id TEXT NOT NULL,
    ts TEXT NOT NULL,
    device TEXT NOT NULL,
    mountpoint TEXT,
    used_bytes INTEGER,
    total_bytes INTEGER,
    fs_type TEXT,
    PRIMARY KEY (agent_id, ts, device)
);

CREATE TABLE IF NOT EXISTS interface_snapshots (
    agent_id TEXT NOT NULL,
    ts TEXT NOT NULL,
    iface TEXT NOT NULL,
    ip TEXT,
    mac TEXT,
    rx_bytes INTEGER,
    tx_bytes INTEGER,
    rx_packets INTEGER,
    tx_packets INTEGER,
    is_up INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (agent_id, ts, iface)
);

CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    type TEXT NOT NULL,
    severity TEXT NOT NULL,
    source_kind TEXT,
    source_id TEXT,
    summary TEXT NOT NULL,
    detail_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);
CREATE INDEX IF NOT EXISTS idx_events_type ON events(type);
CREATE INDEX IF NOT EXISTS idx_events_severity ON events(severity);

CREATE TABLE IF NOT EXISTS tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL
);

CREATE TABLE IF NOT EXISTS tag_assignments (
    tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    target_kind TEXT NOT NULL,
    target_id TEXT NOT NULL,
    PRIMARY KEY (tag_id, target_kind, target_id)
);
