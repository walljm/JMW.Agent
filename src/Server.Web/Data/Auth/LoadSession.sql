WITH session AS (
UPDATE user_sessions
SET
    last_seen = now()
WHERE
      session_id = $1
  AND expires_at > now() RETURNING user_id
)
SELECT
    s.user_id
  , u.username
  , u.role
FROM
    session    s
    JOIN users u
    ON u.user_id = s.user_id
