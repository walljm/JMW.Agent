-- 0027 duration format: convert numeric interval/lifetime values to
-- human-friendly duration strings (e.g. 86400 → "1d", 168 hours → "7d").
-- Uses UPDATE ... WHERE to only touch rows with the known default values;
-- custom values are left untouched and handled by the backward-compatible
-- parser in Go.

-- Agent intervals: stored as integer seconds → duration strings.
UPDATE config SET value = '30s'  WHERE key = 'agent.heartbeat_interval_secs'  AND value = '30';
UPDATE config SET value = '5m'   WHERE key = 'agent.discovery_interval_secs'  AND value = '300';
UPDATE config SET value = '1d'   WHERE key = 'agent.inventory_interval_secs'  AND value = '86400';

-- Terrain poll interval: stored as integer seconds → duration string.
UPDATE config SET value = '1m'   WHERE key = 'terrain.poll_interval_secs'     AND value = '60';

-- Session lifetime: stored as integer hours → duration string.
UPDATE config SET value = '7d'   WHERE key = 'auth.session_lifetime_hours'    AND value = '168';

-- Retention: convert Go duration strings (hours-only) to day-based where whole.
UPDATE config SET value = '2d'   WHERE key = 'retention.raw_metrics'          AND value = '48h';
UPDATE config SET value = '7d'   WHERE key = 'retention.rollup_5min'          AND value = '168h';
UPDATE config SET value = '90d'  WHERE key = 'retention.rollup_hourly'        AND value = '2160h';
UPDATE config SET value = '365d' WHERE key = 'retention.rollup_daily'         AND value = '8760h';
UPDATE config SET value = '7d'   WHERE key = 'retention.removed_containers'   AND value = '168h';
UPDATE config SET value = '30d'  WHERE key = 'retention.stale_observations'   AND value = '720h';
