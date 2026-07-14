SELECT
    disk
  , name
  , model
  , size_bytes
  , type
  , smart_health
  , smart_temp_c
  , updated_at
FROM
    proj_disks
WHERE
    device = $1
ORDER BY
    name ASC NULLS LAST
  , disk ASC
