INSERT INTO targets
(
      agent_id
    , endpoint
    , collector_type
    , credential_id
    , label
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , $5
    ) RETURNING target_id
