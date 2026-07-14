INSERT INTO credentials
(
      name
    , type
    , encrypted_blob
)
VALUES
    (
          $1
        , $2
        , $3
    ) RETURNING credential_id, NAME, TYPE, created_at
