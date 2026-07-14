SELECT
    filesystem
  , fs_type
  , total_bytes
  , used_bytes
  , free_bytes
  , used_pct
  , updated_at
FROM
    proj_filesystems
WHERE
    device = $1
ORDER BY
    filesystem ASC
