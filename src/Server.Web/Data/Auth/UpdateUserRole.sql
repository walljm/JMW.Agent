UPDATE users
SET
    role = $2
WHERE
    user_id = $1 RETURNING user_id
