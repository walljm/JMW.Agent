-- Phase 5: Expanded metric snapshots and rollup tables.

-- Temperature snapshots: per-sensor time series.
CREATE TABLE IF NOT EXISTS temperature_snapshots (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    ts TEXT NOT NULL,
    sensor TEXT NOT NULL,
    celsius REAL NOT NULL,
    PRIMARY KEY (agent_id, ts, sensor)
);

-- Battery snapshots: charge/health over time.
CREATE TABLE IF NOT EXISTS battery_snapshots (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    ts TEXT NOT NULL,
    charge_pct REAL NOT NULL,
    state TEXT NOT NULL DEFAULT '',
    health_pct REAL,
    PRIMARY KEY (agent_id, ts)
);

-- Process snapshots: top-N processes at a point in time.
CREATE TABLE IF NOT EXISTS process_snapshots (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    ts TEXT NOT NULL,
    pid INTEGER NOT NULL,
    name TEXT NOT NULL,
    cpu_pct REAL,
    mem_bytes INTEGER,
    PRIMARY KEY (agent_id, ts, pid)
);

-- Rollup: 5-minute aggregates for system metrics.
CREATE TABLE IF NOT EXISTS metric_rollup_5min (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    bucket TEXT NOT NULL,
    cpu_pct_avg REAL,
    cpu_pct_max REAL,
    mem_used_avg INTEGER,
    mem_used_max INTEGER,
    load_1_avg REAL,
    sample_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (agent_id, bucket)
);

-- Rollup: hourly aggregates.
CREATE TABLE IF NOT EXISTS metric_rollup_hourly (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    bucket TEXT NOT NULL,
    cpu_pct_avg REAL,
    cpu_pct_max REAL,
    mem_used_avg INTEGER,
    mem_used_max INTEGER,
    load_1_avg REAL,
    sample_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (agent_id, bucket)
);

-- Rollup: daily aggregates.
CREATE TABLE IF NOT EXISTS metric_rollup_daily (
    agent_id TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    bucket TEXT NOT NULL,
    cpu_pct_avg REAL,
    cpu_pct_max REAL,
    mem_used_avg INTEGER,
    mem_used_max INTEGER,
    load_1_avg REAL,
    sample_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (agent_id, bucket)
);
CREATE INDEX IF NOT EXISTS idx_rollup_5min_bucket ON metric_rollup_5min(bucket);
CREATE INDEX IF NOT EXISTS idx_rollup_hourly_bucket ON metric_rollup_hourly(bucket);
CREATE INDEX IF NOT EXISTS idx_rollup_daily_bucket ON metric_rollup_daily(bucket);
