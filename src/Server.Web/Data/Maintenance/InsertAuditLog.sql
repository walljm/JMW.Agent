INSERT INTO audit_log
(
      actor
    , action
    , target_ref
    , detail
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
    ) RETURNING id
