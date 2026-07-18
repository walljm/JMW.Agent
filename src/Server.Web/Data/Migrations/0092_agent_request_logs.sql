-- Lets an admin request that an agent upload its recent console/journald log output for
-- on-demand viewing in the Fleet UI (cache-clear events, collector/scanner failures,
-- self-update activity). Same request/heartbeat/marker shape as clear_trackers_requested_at
-- (see 0070_agent_clear_trackers.sql): NULL means no pull has ever been requested; the agent
-- persists the timestamp it last acted on locally and compares against this value each
-- heartbeat. logs_requested_lines is the page size the admin asked for; logs_requested_before
-- is an opaque paging token (a ring-buffer Seq or a journald __CURSOR) the server relays back
-- to the agent verbatim — it never parses it. The log TEXT itself is never persisted here; it
-- lives only in a short-TTL in-memory cache on the server (AgentLogCache).
ALTER TABLE agents
    ADD COLUMN IF NOT EXISTS logs_requested_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS logs_requested_lines INT,
    ADD COLUMN IF NOT EXISTS logs_requested_before TEXT;
