INSERT INTO
    user_preferences (user_id, pref_key, pref_value, updated_at)
VALUES
    ($1, $2, $3, now())
ON CONFLICT (user_id, pref_key) DO
UPDATE
SET
    pref_value = EXCLUDED.pref_value
  , updated_at = now()
RETURNING
    user_id
