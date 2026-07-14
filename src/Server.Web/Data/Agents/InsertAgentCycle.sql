INSERT INTO agent_cycles
(
      agent_id
    , cycle_at
    , duration_ms
    , facts_sent
    , error_count
    , collectors
    , scanners
    , device_scanners
    , services
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , $5
        , $6
        , $7
        , $8
        , $9
    ) RETURNING cycle_id
