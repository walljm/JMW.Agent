DELETE
FROM
    user_sessions
WHERE
    session_id = $1 RETURNING session_id
