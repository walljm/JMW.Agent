SELECT
    r.service
  , r.zone
  , r.record
  , r.rtype
  , coalesce(r.ip, r.target) AS VALUE
  , r.ttl
FROM
    proj_dns_records r
WHERE
    (
    $1::text IS NULL
  OR r.record ILIKE '%' || $1 || '%'
  OR COALESCE (
    r.ip
   , r.target) ILIKE '%' || $1 || '%'
  OR r.zone ILIKE '%' || $1 || '%'
    )
 AND (
    $2::text IS NULL
  OR (
    r.service
   , r.zone
   , r.record
   , r.rtype)
   > (
    $2::text
   , $3::text
   , $4::text
   , $5::text)
    )
ORDER BY
    r.service ASC
  , r.zone    ASC
  , r.record  ASC
  , r.rtype   ASC
    LIMIT $6
