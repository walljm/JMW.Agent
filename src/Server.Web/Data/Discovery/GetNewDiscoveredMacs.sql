-- Identity signals (serials/UUIDs/os) read from materialization_facts
-- (docs/plans/architecture-identity-facts.md §5 Phase 2d); mac/hostname/vendor/model stay on
-- proj_discovered (§2). LEFT JOIN, not JOIN: a MAC-only row (no serial/uuid/os ever observed)
-- still qualifies here — unlike GetNewDiscoveredSerials, this pass doesn't require any of them.
SELECT
    d.mac
  , coalesce(d.discovered, NULL) AS ip  -- report nullable to match the string? tuple
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
    LEFT JOIN (
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
WHERE
      d.mac IS NOT NULL
  -- Exclude Google Wifi/OnHub rows whose mac was filled in by obscured-MAC reconstruction
  -- (SetDiscoveredMac.sql) rather than direct observation — same guard every other consumer
  -- of proj_discovered.mac already applies (GetDeviceAllFacts.sql, GetDeviceSightings.sql,
  -- DeviceListApi.cs, GetPromotionGapRows.sql). Without it, a reconstructed guess gets promoted
  -- into device_fingerprints as an ordinary, trusted fp_type='mac' row — the fingerprinted_macs
  -- view (0049) only recognizes fp_type='mac', so MaterializeObscuredMacsAsync tagging this same
  -- value as FingerprintType.ObscuredMac first does NOT stop this anti-join from re-promoting
  -- it as 'mac' right after. Every downstream consumer that trusts device_fingerprints
  -- (ResolveMacDevice.sql, ResolveIpDevice.sql, L2 topology, gateway resolution, etc.) inherits
  -- whatever leaks in here, so this is the one place that needs to stop it.
  AND d.obscured_mac IS NULL
  AND NOT exists
          (
              SELECT
                  1
              FROM
                  fingerprinted_macs fm
              WHERE
                  fm.mac = d.mac
              )
