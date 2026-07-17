-- Identity signals (serials/UUIDs/os) read from materialization_facts
-- (docs/plans/architecture-identity-facts.md §5 Phase 2c); mac/hostname/vendor/model stay on
-- proj_discovered (§2).
SELECT
    NULL::text    AS mac
  , coalesce(d.discovered, NULL) AS ip -- report nullable to match the string? tuple
  , d.hostname
  , idf.onvif_serial
  , idf.roku_serial
  , idf.snmp_serial
  , idf.ssdp_uuid
  , idf.wsd_uuid
  , d.vendor
  , d.model
  , idf.os
FROM
    proj_discovered d
    JOIN (
        SELECT
            device
          , entity_key
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].OnvifSerial') AS onvif_serial
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].RokuSerial')  AS roku_serial
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].SnmpSerial')  AS snmp_serial
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].SsdpUuid')    AS ssdp_uuid
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].WsdUuid')     AS wsd_uuid
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].Os')          AS os
        FROM
            materialization_facts
        WHERE
            attribute_path IN (
                'Device[].Discovered[].OnvifSerial', 'Device[].Discovered[].RokuSerial',
                'Device[].Discovered[].SnmpSerial', 'Device[].Discovered[].SsdpUuid',
                'Device[].Discovered[].WsdUuid', 'Device[].Discovered[].Os'
            )
        GROUP BY
            device, entity_key
    ) idf ON idf.device = d.device AND idf.entity_key = d.discovered
        -- LEFT JOIN anti-join: each join can use the (fp_type, fp_value) primary key
        -- index directly, unlike the OR-based correlated NOT EXISTS it replaces.
    LEFT JOIN device_fingerprints f1
    ON f1.fp_type = 'chassis-serial'
        AND f1.fp_value = idf.onvif_serial
        AND idf.onvif_serial IS NOT NULL
    LEFT JOIN device_fingerprints f2
    ON f2.fp_type = 'chassis-serial'
        AND f2.fp_value = idf.roku_serial
        AND idf.roku_serial IS NOT NULL
    LEFT JOIN device_fingerprints f3
    ON f3.fp_type = 'uuid'
        AND f3.fp_value = idf.ssdp_uuid
        AND idf.ssdp_uuid IS NOT NULL
    LEFT JOIN device_fingerprints f4
    ON f4.fp_type = 'uuid'
        AND f4.fp_value = idf.wsd_uuid
        AND idf.wsd_uuid IS NOT NULL
    LEFT JOIN device_fingerprints f5
    ON f5.fp_type = 'chassis-serial'
        AND f5.fp_value = idf.snmp_serial
        AND idf.snmp_serial IS NOT NULL
WHERE
      d.mac IS NULL
  AND (
          idf.onvif_serial IS NOT NULL
              OR idf.roku_serial IS NOT NULL
              OR idf.snmp_serial IS NOT NULL
              OR idf.ssdp_uuid IS NOT NULL
              OR idf.wsd_uuid IS NOT NULL
          )
  AND f1.fp_value IS NULL
  AND f2.fp_value IS NULL
  AND f3.fp_value IS NULL
  AND f4.fp_value IS NULL
  AND f5.fp_value IS NULL
