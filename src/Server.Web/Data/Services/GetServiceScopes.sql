SELECT
    scope
  , enabled
  , start_address
  , end_address
  , subnet_mask
  , gateway
FROM
    proj_dhcp_scopes
WHERE
    service = $1
ORDER BY
    scope ASC
