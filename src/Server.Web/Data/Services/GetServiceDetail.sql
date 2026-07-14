SELECT
    ps.service
  , ps.service_id
  , ps.type
  , ps.device_id
  , ca.ca_status
  , ca.ca_address
  , ca.root_subject_dn
  , ca.root_not_before
  , ca.root_not_after
  , ca.root_fingerprint
  , ca.int_subject_dn
  , ca.int_not_before
  , ca.int_not_after
  , ds.total_queries
  , ds.total_blocked
  , ds.blocked_pct
FROM
    proj_services             ps
    LEFT JOIN proj_service_ca ca
    ON ca.service = ps.service
    LEFT JOIN proj_dns_stats  ds
    ON ds.service = ps.service
WHERE
    ps.service = $1
