-- Already-registered devices whose proj_hardware/proj_systems intrinsic fields are still null
-- (or missing entirely), where a passive-observation source already carries a value that could
-- fill the gap — re-evaluated every materialize pass rather than gated to first-mint.
--
-- GetNewDiscoveredMacs.sql / GetNewDiscoveredSerials.sql / GetNewDhcpMacs.sql only promote
-- once, the first time a MAC/serial/UUID is ever fingerprinted — so a device fingerprinted
-- early by a bare ARP sighting (no vendor/model/os) or a DHCP lease (no vendor/model at all)
-- never revisits that promotion once minted, even when a scanner identifies it days later.
-- This pass closes that gap unconditionally on every cycle instead.
--
-- Covers both device populations:
--   - MAC-identified devices: matched to proj_discovered by MAC, with a DHCP-lease hostname
--     (proj_dhcp_leases / proj_dhcp_local_leases) as a further fallback when no scanner ever
--     recorded a hostname for that MAC.
--   - Serial/UUID-identified devices (ONVIF/Roku/SNMP serial, SSDP/WSD UUID — no MAC at all,
--     e.g. a device behind a firewall a MAC-based observer can't see): matched to proj_discovered
--     by whichever identifier the device was actually resolved through.
--
-- Self-limiting: once UpsertDeviceHardware/UpsertDeviceSystem fills a gap, that column is no
-- longer NULL and the device drops out of future scans (see migration 0058 for the partial
-- indexes that keep the "already caught up" steady state cheap).
WITH device_keys AS (
    SELECT
        fp.device_id
      , MAX(fp.fp_value) FILTER (WHERE fp.fp_type = 'mac')            AS mac
      , MAX(fp.fp_value) FILTER (WHERE fp.fp_type = 'chassis-serial') AS serial
      , MAX(fp.fp_value) FILTER (WHERE fp.fp_type = 'uuid')           AS uuid
    FROM device_fingerprints fp
    WHERE fp.fp_type IN ('mac', 'chassis-serial', 'uuid')
    GROUP BY fp.device_id
),
gapped_devices AS (
    SELECT dk.device_id, dk.mac, dk.serial, dk.uuid
    FROM device_keys dk
        LEFT JOIN proj_hardware h ON h.device = dk.device_id::text
        LEFT JOIN proj_systems  s ON s.device = dk.device_id::text
    WHERE h.device IS NULL
       OR s.device IS NULL
       OR h.system_vendor IS NULL
       OR h.system_model IS NULL
       OR s.os_family IS NULL
       OR s.hostname IS NULL
       OR s.friendly_name IS NULL
)
SELECT
    gd.device_id::text AS device
  , sightings.vendor
  , sightings.model
  , sightings.os
  , COALESCE(sightings.hostname, dhcp.hostname) AS hostname
  , sightings.friendly_name
FROM gapped_devices gd
    LEFT JOIN LATERAL (
        -- d.obscured_mac IS NULL excludes Google Wifi/OnHub rows whose `mac` was filled in by
        -- obscured-MAC reconstruction (SetDiscoveredMac.sql) rather than direct observation: a
        -- stale mDNS advertisement can share an IP/MAC with a totally different device once
        -- reconstructed, and that source already has its own careful, every-pass promotion
        -- path (MaterializeObscuredMacsAsync's cast-id/obscured-mac-keyed resolve) that never
        -- keys off the reconstructed MAC for exactly this reason. Matching on it again here
        -- would smear that row's name/model onto whatever device the MAC actually resolves to.
        --
        -- One scan of proj_discovered instead of five: each column independently needs "the
        -- most recent candidate row where THAT column is non-null" (a single most-recent-row
        -- pick would wrongly return null for a field the freshest row happens to lack, even
        -- when an older row has it) — array_agg(...) FILTER (WHERE col IS NOT NULL) ORDER BY
        -- updated_at DESC keeps that per-column semantics; [1] takes the freshest surviving
        -- element. The obscured_mac guard is written once here instead of once per column.
        -- hostname is the genuine mDNS/DHCP-observed hostname only — friendly_name (mDNS fn=/
        -- UPnP friendlyName) is kept separate and promoted to proj_systems.friendly_name, never
        -- folded into the real hostname.
        --
        -- Serial/UUID matching AND os read from materialization_facts (docs/plans/
        -- architecture-identity-facts.md §5 Phase 2f): the match predicate can no longer read
        -- proj_discovered's onvif_serial/roku_serial/snmp_serial/ssdp_uuid/wsd_uuid directly
        -- (those columns are retired in Phase 3), so idf pivots them alongside os. os is ordered
        -- by its own updated_at (carried through the pivot as os_updated_at) rather than the wide
        -- row's shared one — strictly more precise, since the wide row's updated_at is
        -- GREATEST()'d across every column touched that batch, not just os.
        SELECT
            (array_agg(d.vendor ORDER BY d.updated_at DESC)
                FILTER (WHERE d.vendor IS NOT NULL))[1] AS vendor
          , (array_agg(d.model ORDER BY d.updated_at DESC)
                FILTER (WHERE d.model IS NOT NULL))[1] AS model
          , (array_agg(idf.os ORDER BY idf.os_updated_at DESC)
                FILTER (WHERE idf.os IS NOT NULL))[1] AS os
          , (array_agg(d.hostname ORDER BY d.updated_at DESC)
                FILTER (WHERE d.hostname IS NOT NULL))[1] AS hostname
          , (array_agg(COALESCE(d.friendly_name, d.hostname) ORDER BY d.updated_at DESC)
                FILTER (WHERE COALESCE(d.friendly_name, d.hostname) IS NOT NULL))[1] AS friendly_name
        FROM proj_discovered d
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
              , MAX(updated_at) FILTER (WHERE attribute_path = 'Device[].Discovered[].Os')     AS os_updated_at
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
        WHERE d.obscured_mac IS NULL
          AND (d.mac = gd.mac OR idf.onvif_serial = gd.serial OR idf.roku_serial = gd.serial
               OR idf.snmp_serial = gd.serial OR idf.ssdp_uuid = gd.uuid OR idf.wsd_uuid = gd.uuid)
    ) sightings ON TRUE
    LEFT JOIN LATERAL (
        -- DHCP leases only ever carry mac + hostname (no vendor/model/os) — a further
        -- fallback for MAC-identified devices a scanner never named.
        SELECT hostname
        FROM (
            SELECT hostname, updated_at FROM proj_dhcp_leases WHERE lease = gd.mac AND hostname IS NOT NULL
            UNION ALL
            SELECT hostname, updated_at FROM proj_dhcp_local_leases WHERE lease = gd.mac AND hostname IS NOT NULL
        ) leases
        ORDER BY updated_at DESC LIMIT 1
    ) dhcp ON TRUE
