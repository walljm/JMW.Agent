SELECT
    provisioner
  , provisioner_type
  , default_duration
FROM
    proj_service_ca_provisioners
WHERE
    service = $1
ORDER BY
    provisioner ASC
