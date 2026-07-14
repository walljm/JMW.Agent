INSERT INTO user_sessions
(
      session_id
    , user_id
    , expires_at
    , user_agent
    , ip_address
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , $5
    ) RETURNING session_id
