ALTER TABLE devices ADD COLUMN agent_id TEXT;
CREATE INDEX IF NOT EXISTS idx_devices_agent_id ON devices(agent_id);
