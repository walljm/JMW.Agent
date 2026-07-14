SELECT
    ZONE
  , record
  , rtype
  , COALESCE (
    ip
  , target) AS VALUE
  , ttl
FROM
    proj_dns_records
WHERE
    service = $1
ORDER BY
    ZONE   ASC
  , record ASC
  , rtype  ASC
