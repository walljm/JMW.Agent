-- Indexes backing the redesigned Fleet Dashboard aggregates (SCR-003).
-- Two hot paths that had no supporting index before:
--   1. "New devices" — devices first observed in the last N days, newest first.
--   2. "Reporting (24h)" / "Not seen recently" — devices whose newest fingerprint
--      last_seen falls inside / outside a recency window.
-- Other dashboard aggregates reuse existing indexes: agents(status), agent_cycles(agent_id,
-- cycle_at DESC), facts_history(collected_at DESC), and the partial projection-status indexes
-- (proj_disks.smart_health, proj_containers.state, proj_hardware_inventory.status). The agents
-- table is small enough that the heartbeat-liveness scan needs no index.

-- New-device feed: devices ordered by created_at, newest first.
CREATE INDEX if NOT EXISTS devices_created_at_idx
    ON jmwdiscovery.devices (created_at DESC);

-- Device recency: "is any fingerprint newer than the cutoff?" for reporting-vs-quiet and the
-- not-seen list. Composite so the per-device recency probe is index-served.
CREATE INDEX if NOT EXISTS device_fingerprints_last_seen_idx
    ON jmwdiscovery.device_fingerprints (last_seen DESC);
