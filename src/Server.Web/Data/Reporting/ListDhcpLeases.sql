SELECT
    l.device
  , s.hostname          AS observer_hostname
  , l.lease             AS mac
  , oui_vendor(l.lease) AS oui
  , oui_country(l.lease) AS oui_country
  , l.ip
  , l.hostname          AS client_hostname
  , l.expires_at
  , l.source
FROM
    proj_dhcp_local_leases l
    LEFT JOIN proj_systems s
    ON s.device = l.device
WHERE
      (
          $1::text IS NULL
     OR l.ip         ILIKE '%' || $1 || '%'
     OR l.hostname   ILIKE '%' || $1 || '%'
     OR l.lease      ILIKE '%' || $1 || '%'
     OR s.hostname   ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
     OR (l.device, l.lease) > ($2::text, $3::text)
          )
ORDER BY
    l.device ASC
  , l.lease  ASC LIMIT $4
