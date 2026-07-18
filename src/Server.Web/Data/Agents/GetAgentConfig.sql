SELECT
    agent_id
  , hostname
  , status
  , heartbeat_interval_secs
  , discovery_interval_secs
  , inventory_interval_secs
  , collectors_config
  , clear_trackers_requested_at
  , logs_requested_at
  , logs_requested_lines
  , logs_requested_before
FROM
    agents
WHERE
    agent_id = $1
