SET
search_path TO jmwdiscovery, PUBLIC;

-- Per-agent configuration delivered to the agent via the heartbeat config block.
-- collectors_config holds collector enable/disable state and per-collector
-- interval overrides as JSONB, keyed by collector class name:
--   {"OsCollector": {"enabled": true, "interval_secs": 300}, ...}
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS collectors_config JSONB NOT NULL DEFAULT '{}';
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS heartbeat_interval_secs INTEGER NOT NULL DEFAULT 30;
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS discovery_interval_secs INTEGER NOT NULL DEFAULT 300;
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS inventory_interval_secs INTEGER NOT NULL DEFAULT 86400;

-- updated_at supports config-edit RETURNING and "last changed" display.
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();
