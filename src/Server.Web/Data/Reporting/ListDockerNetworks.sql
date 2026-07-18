SELECT
    device
  , dockernet AS cidr
  , name
  , driver
  , scope
FROM
    proj_docker_networks
ORDER BY
    device    ASC
  , dockernet ASC
