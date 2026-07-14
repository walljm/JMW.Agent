UPDATE agents
SET
    last_heartbeat         = now()
  , version                = coalesce($2, version)
  , passive_discovery_mode = coalesce($3, passive_discovery_mode)
  , capabilities           = coalesce($4, capabilities)
WHERE
    agent_id = $1 RETURNING status, os, arch
