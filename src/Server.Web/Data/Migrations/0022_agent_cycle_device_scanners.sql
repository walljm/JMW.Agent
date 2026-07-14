-- Add device_scanners to agent_cycles.
-- Remote device collectors (ssh/snmp/google-wifi "device scanners") now report a
-- per-target activity stat per cycle, alongside the existing local `collectors`
-- and network `scanners` arrays. Stored as JSONB for the Activity view.
ALTER TABLE jmwdiscovery.agent_cycles
    ADD COLUMN if NOT EXISTS device_scanners JSONB NOT NULL DEFAULT '[]';
