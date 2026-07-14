SELECT
    NULL::text    AS mac
  , coalesce(d.discovered, NULL) AS ip -- report nullable to match the string? tuple
  , d.hostname
  , d.onvif_serial
  , d.roku_serial
  , d.snmp_serial
  , d.ssdp_uuid
  , d.wsd_uuid
  , d.vendor
  , d.model
  , d.os
FROM
    proj_discovered               d
        -- LEFT JOIN anti-join: each join can use the (fp_type, fp_value) primary key
        -- index directly, unlike the OR-based correlated NOT EXISTS it replaces.
    LEFT JOIN device_fingerprints f1
    ON f1.fp_type = 'chassis-serial'
        AND f1.fp_value = d.onvif_serial
        AND d.onvif_serial IS NOT NULL
    LEFT JOIN device_fingerprints f2
    ON f2.fp_type = 'chassis-serial'
        AND f2.fp_value = d.roku_serial
        AND d.roku_serial IS NOT NULL
    LEFT JOIN device_fingerprints f3
    ON f3.fp_type = 'uuid'
        AND f3.fp_value = d.ssdp_uuid
        AND d.ssdp_uuid IS NOT NULL
    LEFT JOIN device_fingerprints f4
    ON f4.fp_type = 'uuid'
        AND f4.fp_value = d.wsd_uuid
        AND d.wsd_uuid IS NOT NULL
    LEFT JOIN device_fingerprints f5
    ON f5.fp_type = 'chassis-serial'
        AND f5.fp_value = d.snmp_serial
        AND d.snmp_serial IS NOT NULL
WHERE
      d.mac IS NULL
  AND (
          d.onvif_serial IS NOT NULL
              OR d.roku_serial IS NOT NULL
              OR d.snmp_serial IS NOT NULL
              OR d.ssdp_uuid IS NOT NULL
              OR d.wsd_uuid IS NOT NULL
          )
  AND f1.fp_value IS NULL
  AND f2.fp_value IS NULL
  AND f3.fp_value IS NULL
  AND f4.fp_value IS NULL
  AND f5.fp_value IS NULL
