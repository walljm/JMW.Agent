-- Add per-cycle service-collection stats to agent_cycles, so the agent Activity view shows
-- how much data each service target (DNS, Home Assistant, …) collected. Previously service
-- collection was posted in a separate request with no cycle summary and went unrecorded.
ALTER TABLE jmwdiscovery.agent_cycles
    ADD COLUMN if NOT EXISTS services JSONB NOT NULL DEFAULT '[]';
