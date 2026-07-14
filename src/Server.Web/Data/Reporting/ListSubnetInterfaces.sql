SELECT
    i.device
  , s.hostname AS hostname
  , i.name
  , i.ipv4
  , i.ipv4_prefix_length
FROM
    proj_interfaces      i
    LEFT JOIN proj_systems s
    ON s.device = i.device
WHERE
    i.ipv4 IS NOT NULL
  AND (
        i.ipv4 LIKE '%/%'
     OR i.ipv4_prefix_length IS NOT NULL
      )
ORDER BY
    i.device ASC
  , i.name   ASC
