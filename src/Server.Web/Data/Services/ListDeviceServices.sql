SELECT
    ps.service
  , ps.type
  , ca.ca_status
  , ca.root_not_after
  , ds.total_queries
  , ds.blocked_pct
FROM
    proj_services             ps
    LEFT JOIN proj_service_ca ca
    ON ca.service = ps.service
    LEFT JOIN proj_dns_stats  ds
    ON ds.service = ps.service
WHERE
    ps.device_id = $1
ORDER BY
    ps.service ASC
