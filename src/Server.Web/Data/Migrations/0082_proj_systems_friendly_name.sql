-- Splits the display "friendly name" (mDNS/UPnP/Home-Assistant name, or an operator override)
-- out of proj_systems.hostname, which used to be conflated with it. hostname now holds ONLY
-- the real, agent-/mDNS-reported OS hostname; friendly_name is the display rollup. See
-- FactPaths.SystemFriendlyName and ProjectionLibrary's proj_systems entry.

ALTER TABLE proj_systems ADD COLUMN IF NOT EXISTS friendly_name text;

CREATE INDEX IF NOT EXISTS ix_proj_systems_friendly_name_trgm
    ON proj_systems USING gin (friendly_name gin_trgm_ops);
