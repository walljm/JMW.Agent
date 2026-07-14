UPDATE users
SET
    password_hash = $2
WHERE
    user_id = $1 RETURNING user_id
