INSERT INTO users
(
      username
    , password_hash
    , role
)
VALUES
    (
          $1
        , $2
        , 'viewer'
    ) RETURNING username
