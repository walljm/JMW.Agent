-- Everything currently known about a device, from the raw fact log.
--   • "identity"        — the device's own fingerprints. A device exists ONLY because at
--                         least one of these was observed, so this branch guarantees the
--                         view is never empty for a device whose page exists.
--   • "own"             — facts_history rows keyed directly to this device (agent/SNMP).
--   • "sighting"        — Discovered[] facts an observer recorded about this device,
--                         matched via the observer's proj_discovered row that shares any
--                         fingerprint value with this device.
--   • "own_metrics"     — same shape as "own" but sourced from metrics_raw: monotonic
--                         counters (interface Rx/Tx bytes/packets, TotalBytes — see
--                         FactPaths.MetricPaths) never land in facts_history, so their
--                         current value has to come from here instead. Without this branch
--                         the device-detail "Interface Counters" table goes blank the
--                         moment a fact is metric-classified. See docs/plans/metrics-retention.md §2.6.
--   • "sighting_metrics" — same as "sighting" but for Discovered[] link counters
--                         (DiscoveredLinkRxBytes/TxBytes), sourced from metrics_raw for the
--                         same reason.
-- "own"/"sighting" explicitly exclude metric-classified paths: after cutover no new
-- facts_history rows are ever written for them, so any surviving row is a frozen
-- pre-cutover value that would otherwise sit alongside the live metrics_raw value with no
-- deterministic tiebreak between the two.
-- DISTINCT ON collapses the append log to the latest value per fact id.
WITH idf AS (
    -- Pivot of the identity-signal paths that moved off proj_discovered's wide columns onto
    -- materialization_facts (docs/plans/architecture-identity-facts.md §5) — the "sighting"/
    -- "sighting_metrics" CTEs below need these (alongside mac, which stays wide) to match a
    -- discovered row's identity to this device's own fingerprints. snmp_serial/os/etc. aren't
    -- part of this match today (pre-existing scope — this file only ever matched on
    -- mac/ssdp_uuid/wsd_uuid/onvif_serial/roku_serial), so only those four are pivoted here.
    SELECT
        device
      , entity_key
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].OnvifSerial') AS onvif_serial
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].RokuSerial')  AS roku_serial
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].SsdpUuid')    AS ssdp_uuid
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].WsdUuid')     AS wsd_uuid
    FROM
        materialization_facts
    WHERE
        attribute_path IN (
            'Device[].Discovered[].OnvifSerial', 'Device[].Discovered[].RokuSerial',
            'Device[].Discovered[].SsdpUuid', 'Device[].Discovered[].WsdUuid'
        )
    GROUP BY
        device, entity_key
)
  , identity AS (
    SELECT
        'Identity.' || fp.fp_type AS attribute_path
      , NULL::jsonb                AS key_values
      , fp.fp_value
    AS VALUE
   , 'identity' AS origin
   , NULL::text AS source_name
   , d.created_at AS collected_at
FROM
    device_fingerprints fp
    JOIN devices d
ON d.device_id = fp.device_id
WHERE
    fp.device_id = $1
    )
  , own AS (
SELECT
    DISTINCT
ON (h.id)
    h.attribute_path
    , h.key_values AS key_values
    , COALESCE (h.value_str, h.value_long::text, h.value_double::text) AS VALUE
    , 'own' AS origin
    , h.source_name
    , h.collected_at
FROM
    facts_history h
WHERE
    h.key_values ->> 'Device' = $1::text
    AND h.attribute_path <> ALL (ARRAY[
        'Device[].Interface[].RxBytes',
        'Device[].Interface[].TxBytes',
        'Device[].Interface[].RxPackets',
        'Device[].Interface[].TxPackets',
        'Device[].Interface[].TotalBytes',
        'Device[].Discovered[].Link.RxBytes',
        'Device[].Discovered[].Link.TxBytes'
    ])
ORDER BY
    h.id
  , h.collected_at DESC
    )
  , sighting AS (
SELECT
    DISTINCT
ON (h.id)
    h.attribute_path
    , h.key_values AS key_values
    , COALESCE (h.value_str, h.value_long::text, h.value_double::text) AS VALUE
    , COALESCE (obs.hostname, h.key_values ->> 'Device') AS origin
    , h.source_name
    , h.collected_at
FROM
    proj_discovered d
    LEFT JOIN idf ON idf.device = d.device AND idf.entity_key = d.discovered
    -- Match the observation to this device by ANY shared fingerprint value — a
    -- discovered device is often anchored by mDNS/SSDP UUID or a serial, not its
    -- MAC (and a Google-Wifi MAC only ever arrives obscured), so a mac-only join
    -- misses everything an observer recorded about it.
    -- d.obscured_mac IS NULL: exclude Google Wifi/OnHub rows whose mac was filled in
    -- by obscured-MAC reconstruction rather than direct observation — see the same
    -- guard in DeviceListApi.cs's `disc` lateral / GetPromotionGapRows.sql for why matching
    -- on it here would smear a stale mDNS advertisement's facts onto this device.
    JOIN device_fingerprints f
ON f.device_id = $1
    AND d.obscured_mac IS NULL
    AND f.fp_value IN (d.mac, idf.ssdp_uuid, idf.wsd_uuid, idf.onvif_serial, idf.roku_serial)
    JOIN facts_history h
    ON h.key_values ->> 'Device' = d.device
    AND h.key_values ->> 'Discovered' = d.discovered
    AND h.attribute_path <> ALL (ARRAY[
        'Device[].Interface[].RxBytes',
        'Device[].Interface[].TxBytes',
        'Device[].Interface[].RxPackets',
        'Device[].Interface[].TxPackets',
        'Device[].Interface[].TotalBytes',
        'Device[].Discovered[].Link.RxBytes',
        'Device[].Discovered[].Link.TxBytes'
    ])
    LEFT JOIN proj_systems obs
    ON obs.device = d.device
ORDER BY
    h.id
  , h.collected_at DESC
    )
  , own_metrics AS (
SELECT
    DISTINCT
ON (m.id)
    m.attribute_path
    , m.key_values AS key_values
    , COALESCE (m.value_str, m.value_long::text, m.value_double::text) AS VALUE
    , 'own' AS origin
    , m.source_name
    , m.collected_at
FROM
    metrics_raw m
WHERE
    m.key_values ->> 'Device' = $1::text
ORDER BY
    m.id
  , m.collected_at DESC
    )
  , sighting_metrics AS (
SELECT
    DISTINCT
ON (m.id)
    m.attribute_path
    , m.key_values AS key_values
    , COALESCE (m.value_str, m.value_long::text, m.value_double::text) AS VALUE
    , COALESCE (obs.hostname, m.key_values ->> 'Device') AS origin
    , m.source_name
    , m.collected_at
FROM
    proj_discovered d
    LEFT JOIN idf ON idf.device = d.device AND idf.entity_key = d.discovered
    JOIN device_fingerprints f
ON f.device_id = $1
    AND d.obscured_mac IS NULL
    AND f.fp_value IN (d.mac, idf.ssdp_uuid, idf.wsd_uuid, idf.onvif_serial, idf.roku_serial)
    JOIN metrics_raw m
    ON m.key_values ->> 'Device' = d.device
    AND m.key_values ->> 'Discovered' = d.discovered
    LEFT JOIN proj_systems obs
    ON obs.device = d.device
ORDER BY
    m.id
  , m.collected_at DESC
    )
  , all_facts AS (
        SELECT attribute_path, key_values, value, origin, source_name, collected_at FROM identity
        UNION ALL
        SELECT attribute_path, key_values, value, origin, source_name, collected_at FROM own
        UNION ALL
        SELECT attribute_path, key_values, value, origin, source_name, collected_at FROM sighting
        UNION ALL
        SELECT attribute_path, key_values, value, origin, source_name, collected_at FROM own_metrics
        UNION ALL
        SELECT attribute_path, key_values, value, origin, source_name, collected_at FROM sighting_metrics
    )
-- Same fact, same value, reported by more than one observer (a "sighting" row per observer,
-- since each observer's own key_values.Device/Discovered differ even though it's the same
-- real-world fact) collapses to one row here — attribute_path + value + the row's OTHER list
-- dimensions (key_values minus "Device", the reporter, and "Discovered", the OBSERVER's own
-- neighbor-slot id — neither identifies the fact itself) is the true identity. Observers/
-- collectors that agree on the value are merged into one comma-joined display string per
-- column; own/identity/own_metrics rows are already unique on this key (no "Discovered" key
-- to begin with), so they pass through as one-to-one groups, unaffected.
SELECT
    attribute_path
  , (array_agg(key_values ORDER BY collected_at DESC))[1]::text AS key_values
  , value
  , string_agg(DISTINCT origin, ', ' ORDER BY origin)           AS origin
  , string_agg(DISTINCT source_name, ', ' ORDER BY source_name) AS source_name
  , max(collected_at)                                           AS collected_at
FROM
    all_facts
GROUP BY
    attribute_path, value, (COALESCE(key_values, '{}'::jsonb) - 'Device' - 'Discovered')
ORDER BY
    attribute_path
