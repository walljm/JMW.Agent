SELECT
    a.mac
FROM
    proj_device_arp a
WHERE
      a.mac IS NOT NULL
  AND NOT exists
          (
              SELECT
                  1
              FROM
                  fingerprinted_macs fm
              WHERE
                  fm.mac = a.mac
              )
