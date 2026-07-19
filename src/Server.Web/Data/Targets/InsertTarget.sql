INSERT INTO targets
(
      agent_id
    , endpoint
    , collector_type
    , credential_id
    , label
    , endpoint_kind
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , $5
        , $6
    ) RETURNING target_id
