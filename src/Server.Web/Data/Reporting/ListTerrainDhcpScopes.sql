SELECT
    service
  , scope
  , enabled
  , start_address
  , end_address
  , subnet_mask
  , gateway
FROM
    proj_dhcp_scopes
ORDER BY
    service ASC
  , scope   ASC
