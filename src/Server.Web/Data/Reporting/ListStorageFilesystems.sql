SELECT
    f.device
  , s.hostname
  , f.filesystem
  , f.fs_type
  , f.total_bytes
  , f.used_bytes
  , f.free_bytes
  , f.used_pct
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_filesystems       f
    LEFT JOIN proj_systems s
    ON s.device = f.device
WHERE
    (
        $1::text IS NULL
  OR (
    f.device
   , f.filesystem)
   > (
    $1
   , $2))
ORDER BY
    f.device     ASC
  , f.filesystem ASC LIMIT $3
