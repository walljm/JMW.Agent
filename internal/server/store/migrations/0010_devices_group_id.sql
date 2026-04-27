-- Soft device grouping: multiple MAC rows that represent the same physical
-- device share a non-null device_group_id. NULL means "ungrouped, treat the
-- row as its own group".
--
-- Group ID conventions:
--   agent:{agent_id}    All NICs belonging to one of our managed agents.
--   mdns:{hostname}     Best-effort: rows that share an mDNS hostname.
ALTER TABLE devices ADD COLUMN device_group_id TEXT;
CREATE INDEX IF NOT EXISTS idx_devices_group ON devices(device_group_id);
