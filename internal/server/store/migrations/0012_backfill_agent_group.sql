-- Backfill device_group_id for rows that already belong to a known agent
-- (agent_id is non-empty) but were inserted by an older server build that
-- did not set the group. Without this, every virtual NIC the agent
-- reported once but no longer reports (utun*, awdl0, llw0, …) lingers as
-- its own ungrouped row in the devices list.
--
-- Idempotent: only touches rows where the group is currently NULL or empty.
UPDATE devices
   SET device_group_id = 'agent:' || agent_id
 WHERE agent_id IS NOT NULL
   AND agent_id <> ''
   AND (device_group_id IS NULL OR device_group_id = '');
