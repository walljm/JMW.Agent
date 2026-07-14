SELECT
    agent_id
  , hostname
  , status
  , last_heartbeat
  , ZONE
  , version
  , os
  , arch
  , ip_address
  , device_id
  , heartbeat_interval_secs
  , discovery_interval_secs
  , inventory_interval_secs
  , collectors_config
  , agent_liveness(last_heartbeat, heartbeat_interval_secs) AS liveness
  , capabilities
FROM
    agents
WHERE
    agent_id = $1
