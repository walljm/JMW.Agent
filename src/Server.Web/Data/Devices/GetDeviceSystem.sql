SELECT
    hostname
  , os_family
  , os_distro
  , updated_at
FROM
    proj_systems
WHERE
    device = $1
