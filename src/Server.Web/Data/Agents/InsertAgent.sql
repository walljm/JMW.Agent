INSERT INTO agents
(
      agent_id
    , hostname
    , status
    , api_key_hash
    , zone
    , version
    , passive_discovery_mode
    , os
    , arch
    , ip_address
)
VALUES
    (
          $1
        , $2
        , 'pending'
        , $3
        , $4
        , $5
        , $6
        , $7
        , $8
        , $9
    ) RETURNING agent_id
