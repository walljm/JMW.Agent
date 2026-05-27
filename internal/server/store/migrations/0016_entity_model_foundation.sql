-- 0016 entity model foundation: sources, notification channels, maintenance windows, subsystem registry

-- Pipeline input sources (terrain pollers, future SNMP/Nmap, agent feeds).
CREATE TABLE IF NOT EXISTS sources (
    id TEXT PRIMARY KEY,                           -- server-generated UUID
    name TEXT NOT NULL,                            -- human label
    kind TEXT NOT NULL,                            -- terrain-dhcp | terrain-dns | snmp-poller | nmap-scanner | agent | user-input
    enabled INTEGER NOT NULL DEFAULT 1,
    service_id TEXT,                               -- FK → services (NULL until entity model phase)
    agent_id TEXT REFERENCES agents(id) ON DELETE SET NULL,  -- set only for kind=agent
    config_json TEXT NOT NULL DEFAULT '{}',        -- per-kind config; secret fields encrypted with DEK
    poll_interval_seconds INTEGER,                 -- NULL for push-only (agent)
    last_success_at TEXT,
    last_error_at TEXT,
    last_error_message TEXT,
    consecutive_error_count INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sources_kind ON sources(kind);
CREATE INDEX IF NOT EXISTS idx_sources_enabled ON sources(enabled);

-- Notification delivery targets for alert firings.
CREATE TABLE IF NOT EXISTS notification_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,                            -- email | webhook (future: discord, pushover, gotify, slack)
    config_json TEXT NOT NULL DEFAULT '{}',        -- secret fields encrypted with DEK
    rate_limit_per_hour INTEGER,                   -- NULL = unlimited
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

-- Scheduled alert suppression windows.
CREATE TABLE IF NOT EXISTS maintenance_windows (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    scope_kind TEXT NOT NULL,                      -- agent | tag | source | service | disk | network | hardware | all
    scope_id TEXT NOT NULL DEFAULT '',             -- entity ID or tag name; empty for scope_kind=all
    starts_at TEXT NOT NULL,
    ends_at TEXT NOT NULL,
    reason TEXT NOT NULL DEFAULT '',
    created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_maint_windows_active ON maintenance_windows(starts_at, ends_at);

-- Subsystem registry: canonical list of agent subsystems the server knows about.
CREATE TABLE IF NOT EXISTS subsystems (
    name TEXT PRIMARY KEY,                         -- discovery | inventory | metrics | network-sensor | container-cache | ...
    description TEXT NOT NULL DEFAULT '',
    expected_cadence_seconds INTEGER,              -- NULL = push-driven
    introduced_in_version TEXT NOT NULL DEFAULT '',
    deprecated_in_version TEXT
);
-- Seed known subsystems.
INSERT OR IGNORE INTO subsystems(name, description, expected_cadence_seconds) VALUES
    ('metrics', 'System metric snapshots (CPU, memory, load, disk I/O, network counters)', 30),
    ('discovery', 'Network neighbor sightings (ARP, mDNS, NBNS, etc.)', 300),
    ('inventory', 'Full system inventory (hardware, packages, services, security posture)', 86400),
    ('disk', 'Disk SMART and partition details', 86400),
    ('docker', 'Docker/container runtime state', 30),
    ('smart', 'SMART health collection (subset of disk, kept for legacy compat)', 86400);

-- Junction: which subsystems each agent has enabled.
CREATE TABLE IF NOT EXISTS agent_subsystems (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    subsystem_name TEXT NOT NULL REFERENCES subsystems(name) ON DELETE CASCADE,
    enabled INTEGER NOT NULL DEFAULT 1,
    last_sample_received_at TEXT,
    first_seen_at TEXT NOT NULL,
    PRIMARY KEY (agent_id, subsystem_name)
);

-- Tripwire: agent reports a section the server doesn't know about.
CREATE TABLE IF NOT EXISTS agent_unknown_sections (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    section_name TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    sample_count INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (agent_id, section_name)
);
