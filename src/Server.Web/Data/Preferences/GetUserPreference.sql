SELECT
    pref_value
FROM
    user_preferences
WHERE
    user_id = $1
    AND pref_key = $2
