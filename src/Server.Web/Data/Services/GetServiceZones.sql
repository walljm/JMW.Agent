SELECT
    ZONE
  , zone_type
FROM
    proj_dns_zones
WHERE
    service = $1
ORDER BY
    ZONE ASC
