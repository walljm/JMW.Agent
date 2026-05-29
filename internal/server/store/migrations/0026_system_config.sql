-- 0026 system config: seed operational defaults into the config key/value table.
-- All values use INSERT OR IGNORE so existing customised values are preserved
-- on re-migration or schema replay.

INSERT OR IGNORE INTO config (key, value) VALUES
    -- Agent collection intervals (seconds). Returned to agents on
    -- registration and every heartbeat; agents use these in preference to
    -- their local agent.toml values.
    ('agent.heartbeat_interval_secs',  '30'),
    ('agent.discovery_interval_secs',  '300'),
    ('agent.inventory_interval_secs',  '86400'),

    -- Terrain (Key Cyber Terrain) connection and polling config.
    -- URL is the base URL of the AdGuard/Technitium/Pi-hole instance.
    -- Token is a Technitium API token; username+password are for Pi-hole.
    ('terrain.url',               ''),
    ('terrain.token',             ''),
    ('terrain.username',          ''),
    ('terrain.password',          ''),
    ('terrain.poll_interval_secs', '60'),

    -- Data retention windows. These control how long raw and rolled-up
    -- metric data is kept. Durations are Go time.ParseDuration strings
    -- (e.g. "48h", "7d"). The rollup.go pruner reads these on every tick.
    -- NOTE: "d" is not a valid Go duration unit; values here must use "h".
    ('retention.raw_metrics',        '48h'),
    ('retention.rollup_5min',        '168h'),   -- 7 days
    ('retention.rollup_hourly',      '2160h'),  -- 90 days
    ('retention.rollup_daily',       '8760h'),  -- 365 days
    ('retention.removed_containers', '168h'),   -- 7 days
    ('retention.stale_observations', '720h'),   -- 30 days

    -- Session lifetime in hours.
    ('auth.session_lifetime_hours', '168');
