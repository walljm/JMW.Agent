SELECT
    exists
        (
            SELECT
                1
            FROM
                targets
            WHERE
                credential_id = $1
            ) AS in_use
