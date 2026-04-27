-- Second-pass backfill: collapse device rows whose hostname matches an
-- approved agent's hostname (after normalizing trailing '.' and '.local')
-- into that agent's canonical group.
--
-- macOS Wi-Fi MAC randomization (per-SSID) creates a common case the
-- per-NIC inventory upsert can't fix on its own: another agent ARPs the
-- macbook's current Wi-Fi MAC and tags it `mdns:<hostname>`, while the
-- macbook agent itself reports a different (or rotated) MAC in its
-- inventory. The two sets of rows then refuse to merge in the UI.
--
-- This migration normalizes those rows under `agent:<agent.id>` so they
-- collapse with the agent's own inventory rows. Existing `agent:...`
-- groups are never overwritten (agent has priority 5; mdns=4, nbns=3,
-- rdns=2, unknown=0).
UPDATE devices
   SET device_group_id = 'agent:' || (
         SELECT a.id FROM agents a
          WHERE LOWER(REPLACE(REPLACE(IFNULL(a.hostname,''), '.local', ''), '.', ''))
              = LOWER(REPLACE(REPLACE(IFNULL(devices.hostname,''), '.local', ''), '.', ''))
          LIMIT 1),
       agent_id = COALESCE(NULLIF(agent_id,''), (
         SELECT a.id FROM agents a
          WHERE LOWER(REPLACE(REPLACE(IFNULL(a.hostname,''), '.local', ''), '.', ''))
              = LOWER(REPLACE(REPLACE(IFNULL(devices.hostname,''), '.local', ''), '.', ''))
          LIMIT 1))
 WHERE hostname IS NOT NULL AND hostname <> ''
   AND (device_group_id IS NULL OR device_group_id = '' OR device_group_id NOT LIKE 'agent:%')
   AND EXISTS (
         SELECT 1 FROM agents a
          WHERE LOWER(REPLACE(REPLACE(IFNULL(a.hostname,''), '.local', ''), '.', ''))
              = LOWER(REPLACE(REPLACE(IFNULL(devices.hostname,''), '.local', ''), '.', '')));
