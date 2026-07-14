SELECT
    coalesce(l.lease, NULL) AS mac -- report nullable to match the string? tuple
  , NULL::text    AS ip
  , l.hostname
  , NULL::text    AS onvif_serial
  , NULL::text    AS roku_serial
  , NULL::text    AS snmp_serial
  , NULL::text    AS ssdp_uuid
  , NULL::text    AS wsd_uuid
  , NULL::text    AS vendor
  , NULL::text    AS model
  , NULL::text    AS os
FROM
    proj_dhcp_leases l
WHERE
      l.lease IS NOT NULL
  AND NOT exists
          (
              SELECT
                  1
              FROM
                  fingerprinted_macs fm
              WHERE
                  fm.mac = l.lease
              )
