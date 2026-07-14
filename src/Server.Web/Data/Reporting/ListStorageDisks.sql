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
FROM
    proj_disks             d
    LEFT JOIN proj_systems s
    ON s.device = d.device
WHERE
    (
        $1::text IS NULL
  OR (
    d.device
   , d.disk)
   > (
    $1
   , $2))
ORDER BY
    d.device ASC
  , d.disk   ASC LIMIT $3
