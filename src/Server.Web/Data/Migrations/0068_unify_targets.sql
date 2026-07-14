-- Merges collection_targets + service_targets into one unified `targets` table so the
-- UI/API can present one flow — pick a target, pick a collector type, pick a credential —
-- instead of two parallel, near-identical concepts that only differed because
-- IDeviceCollector and IServiceCollector evolved separately.
--
-- Also reconciles home-assistant + home-assistant-devices rows into a single
-- home-assistant row per agent, ahead of the HomeAssistantCollector merge: the surviving
-- row is the home-assistant-devices one, since it already carries the Core Long-Lived
-- Access Token the merged collector needs (the plain home-assistant row's Supervisor REST
-- fetch never actually authenticated via a stored credential — it only ever worked through
-- the SUPERVISOR_TOKEN env var HA injects into add-on containers). No manual re-add
-- required after this ships.

ALTER TABLE collection_targets RENAME TO targets;
ALTER TABLE targets RENAME COLUMN protocol TO collector_type;
ALTER TABLE targets RENAME COLUMN address TO endpoint;
ALTER INDEX collection_targets_agent_idx RENAME TO targets_agent_idx;
ALTER INDEX collection_targets_keyset_idx RENAME TO targets_keyset_idx;

INSERT INTO targets
    (target_id, agent_id, collector_type, endpoint, credential_id, label, enabled, created_at, updated_at)
SELECT
    service_target_id, agent_id, service_type, url, credential_id, label, enabled, created_at, updated_at
FROM service_targets;

-- Per agent, if both a home-assistant and a home-assistant-devices row exist, drop the
-- superseded home-assistant one.
DELETE FROM targets t
WHERE t.collector_type = 'home-assistant'
  AND EXISTS (
      SELECT 1 FROM targets d
      WHERE d.agent_id = t.agent_id AND d.collector_type = 'home-assistant-devices'
  );

-- Whatever home-assistant-devices row remains (whether or not it had a sibling) becomes
-- the one home-assistant target.
UPDATE targets
SET collector_type = 'home-assistant'
WHERE collector_type = 'home-assistant-devices';

DROP TABLE service_targets;
