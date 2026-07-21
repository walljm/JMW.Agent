SELECT
    d.device
  , s.hostname
  , d.disk
  , d.name
  , d.model
  , d.type
  , d.smart_health
  , d.smart_temp_c
  , d.smart_wear_pct
  , d.smart_power_on_hours
  , d.size_bytes
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_disks             d
    LEFT JOIN proj_systems s
    ON s.device = d.device
WHERE
      (
          $1::text IS NULL
     OR d.device        ILIKE '%' || $1 || '%'
     OR s.hostname      ILIKE '%' || $1 || '%'
     OR s.friendly_name ILIKE '%' || $1 || '%'
     OR d.name          ILIKE '%' || $1 || '%'
     OR d.model         ILIKE '%' || $1 || '%'
     OR d.type          ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
     OR (d.device, d.disk) > ($2::text, $3::text)
          )
ORDER BY
    d.device ASC
  , d.disk   ASC LIMIT $4
