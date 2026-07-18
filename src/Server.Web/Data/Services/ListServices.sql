SELECT
    ps.service
  , ps.type
  , ps.device_id
  , host.friendly_name AS host_friendly_name
  , host.hostname      AS host_hostname
  , host.last_seen_ip  AS host_ip
  , ca.ca_status
  , ca.root_not_after
  , ds.total_queries
  , ds.blocked_pct
FROM
    proj_services              ps
    LEFT JOIN proj_service_ca  ca
    ON ca.service = ps.service
    LEFT JOIN proj_dns_stats   ds
    ON ds.service = ps.service
    LEFT JOIN proj_systems     host
    ON host.device = ps.device_id
WHERE
    (
        $1::text IS NULL
  OR ps.type = $1)
  AND (
        $2::text IS NULL
  OR ps.service ILIKE '%' || $2 || '%'
  OR host.hostname ILIKE '%' || $2 || '%')
  AND (
        $3::text IS NULL
  OR ps.service > $3)
ORDER BY
    ps.service ASC LIMIT $4
