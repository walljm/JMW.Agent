-- Sightings of this device by other observers. proj_discovered rows are keyed by
-- (observer device, neighbor IP); we join to THIS device via its reconstructed MAC
-- fingerprint. Surfaces the per-observer identity + advertised services; per-sighting link
-- telemetry (medium/band/signal/rates/bytes/connected) lives in the "Sighting Telemetry"
-- fact view instead (FactViewLibrary.cs) — display-only data with no join/query need here.
SELECT
    d.device            AS observer_id
  , obs.hostname        AS observer_hostname
  , d.discovered        AS ip
  , d.sources
    -- OUI is the NIC-vendor derived from the MAC at read time (oui != device vendor);
    -- the join guarantees d.mac is non-null for every returned sighting.
  , oui_vendor(d.mac)   AS oui
  , oui_country(d.mac)  AS oui_country
  , (
        SELECT
            string_agg(s.service, ', ' ORDER BY s.service)
        FROM
            proj_discovered_services s
        WHERE
              s.device = d.device
          AND s.discovered = d.discovered
        )               AS services
FROM
    proj_discovered          d
    -- d.obscured_mac IS NULL: exclude Google Wifi/OnHub rows whose mac was filled in by
    -- obscured-MAC reconstruction rather than direct observation — a stale mDNS
    -- advertisement can reconstruct to a totally different device's MAC, and that
    -- sighting belongs to the cast-id identity, not necessarily this one (same guard as
    -- DeviceListApi.cs's `disc` lateral / GetPromotionGapRows.sql).
    JOIN device_fingerprints f
    ON f.fp_type = 'mac' AND f.fp_value = d.mac AND f.device_id = $1
    AND d.obscured_mac IS NULL
    LEFT JOIN proj_systems   obs
    ON obs.device = d.device
ORDER BY
    d.device
  , d.discovered
