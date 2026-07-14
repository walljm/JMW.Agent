SET
search_path TO jmwdiscovery, PUBLIC;

-- Link each agent to the device record for its own host.
-- Populated by the ingest endpoint on first successful fact batch.
ALTER TABLE agents
    ADD COLUMN if NOT EXISTS device_id UUID REFERENCES devices(device_id) ON DELETE SET NULL;
CREATE INDEX if NOT EXISTS agents_device_id_idx ON agents (device_id) WHERE device_id IS NOT NULL;
