SELECT
      u.user_id
    , u.username
    , u.role
    , u.created_at
    , (SELECT MAX(s.last_seen) FROM user_sessions s WHERE s.user_id = u.user_id) AS last_seen
FROM users u
ORDER BY u.created_at
