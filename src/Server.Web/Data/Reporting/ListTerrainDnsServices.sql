SELECT
    service
  , total_queries
  , total_blocked
  , blocked_pct
  , updated_at
FROM
    proj_dns_stats
ORDER BY
    total_queries DESC NULLS LAST
  , service       ASC
