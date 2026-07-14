-- Stores what an agent actually reports it can run (E9) — enumerated collectors/scanners plus
-- each one's IsSupported check for the agent's own OS/platform, sent on every heartbeat.
-- AgentDetail drives its collector/scanner list from this instead of a hardcoded guess once an
-- agent has reported at least once; null (never reported, e.g. an agent build that predates
-- this) falls back to the hardcoded KnownCollectors/KnownScanners list.
--
-- Shape: {"collectors": [{"name": "OsCollector", "supported": true}, ...], "scanners": [...]}
ALTER TABLE agents
    ADD COLUMN capabilities jsonb;
