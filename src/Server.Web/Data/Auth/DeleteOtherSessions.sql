-- Deletes all sessions for $1 (user_id) except $2 (the caller's own session_id).
-- Called on password change to invalidate any stolen or concurrent sessions.
DELETE
FROM
    user_sessions
WHERE
    user_id = $1
  AND session_id <> $2
RETURNING session_id AS session_id
