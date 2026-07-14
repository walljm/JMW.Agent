DELETE
FROM
    targets
WHERE
    target_id = $1 RETURNING target_id
