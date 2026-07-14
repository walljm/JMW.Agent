SELECT
    d.mac
  , COALESCE(d.discovered, NULL) AS ip  -- report nullable to match the string? tuple
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
    proj_discovered d
WHERE
      d.mac IS NOT NULL
  -- Exclude Google Wifi/OnHub rows whose mac was filled in by obscured-MAC reconstruction
  -- (SetDiscoveredMac.sql) rather than direct observation — same guard every other consumer
  -- of proj_discovered.mac already applies (GetDeviceAllFacts.sql, GetDeviceSightings.sql,
  -- HostsApi.cs, GetPromotionGapRows.sql). Without it, a reconstructed guess gets promoted
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
