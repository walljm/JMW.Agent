SELECT
    user_id
  , password_hash
  , role
FROM
    users
WHERE
    username = $1
