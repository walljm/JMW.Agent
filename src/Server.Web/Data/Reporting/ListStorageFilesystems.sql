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
     OR f.device        ILIKE '%' || $1 || '%'
     OR s.hostname      ILIKE '%' || $1 || '%'
     OR s.friendly_name ILIKE '%' || $1 || '%'
     OR f.filesystem    ILIKE '%' || $1 || '%'
     OR f.fs_type       ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
     OR (f.device, f.filesystem) > ($2::text, $3::text)
          )
ORDER BY
    f.device     ASC
  , f.filesystem ASC LIMIT $4
