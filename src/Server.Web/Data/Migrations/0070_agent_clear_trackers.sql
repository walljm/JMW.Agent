-- Lets an admin request that an agent clear its local delta-tracker cache (the
-- CollectorDeltaTracker files each target/service uses to suppress re-sending unchanged
-- facts). Needed when server-side data is reset independently of the agent (e.g. a
-- projection table gets wiped) — the agent's cache still thinks it already reported those
-- facts and would otherwise silently withhold them until something about them changes.
-- NULL means no clear has ever been requested. The agent persists the timestamp it last
-- acted on locally and compares against this value each heartbeat.
ALTER TABLE agents
    ADD COLUMN IF NOT EXISTS clear_trackers_requested_at TIMESTAMPTZ;
