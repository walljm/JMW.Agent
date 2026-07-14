SELECT
    agent_id
  , hostname
  , status
  , heartbeat_interval_secs
  , discovery_interval_secs
  , inventory_interval_secs
  , collectors_config
  , clear_trackers_requested_at
FROM
    agents
WHERE
    agent_id = $1
