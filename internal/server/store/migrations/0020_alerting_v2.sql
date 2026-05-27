-- Phase 6: Alerting v2 — expanded metric targeting, anti-flap, maintenance windows.

-- Expand alert_rules with metric_kind + metric_path columns.
-- metric_kind replaces the old flat "metric" column:
--   numeric_snapshot — value from latest metric_snapshots (cpu_pct, mem_pct, etc.)
--   disk_usage      — per-disk usage percentage
--   temperature     — per-sensor Celsius reading
--   offline         — minutes since last heartbeat
--   posture         — security posture check (AV missing, encryption off, etc.)
--   source_health   — source consecutive error count
ALTER TABLE alert_rules ADD COLUMN metric_kind TEXT NOT NULL DEFAULT 'numeric_snapshot';
ALTER TABLE alert_rules ADD COLUMN metric_path TEXT NOT NULL DEFAULT '';

-- Backfill: migrate old "metric" values to metric_kind/metric_path.
UPDATE alert_rules SET metric_kind = 'numeric_snapshot', metric_path = metric WHERE metric IN ('cpu_pct', 'mem_pct');
UPDATE alert_rules SET metric_kind = 'offline', metric_path = '' WHERE metric = 'offline_minutes';
UPDATE alert_rules SET metric_kind = 'disk_usage', metric_path = '' WHERE metric = 'disk_pct';

-- Anti-flap: track flapping state on firings.
ALTER TABLE alert_firings ADD COLUMN flapping INTEGER NOT NULL DEFAULT 0;
-- Count transitions for flap detection.
ALTER TABLE alert_firings ADD COLUMN transition_count INTEGER NOT NULL DEFAULT 0;

-- Maintenance windows: suppress alert evaluation during scheduled windows.
CREATE TABLE IF NOT EXISTS maintenance_windows (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    target_kind TEXT NOT NULL DEFAULT 'all',
    target_id TEXT NOT NULL DEFAULT '',
    starts_at TEXT NOT NULL,
    ends_at TEXT NOT NULL,
    recurrence TEXT NOT NULL DEFAULT '',   -- '', 'daily', 'weekly'
    created_at TEXT NOT NULL,
    created_by TEXT NOT NULL DEFAULT ''
);

-- Rate limiting on notification channels.
ALTER TABLE notification_channels ADD COLUMN rate_limit_per_hour INTEGER NOT NULL DEFAULT 0;
