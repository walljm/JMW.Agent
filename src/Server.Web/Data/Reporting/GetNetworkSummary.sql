-- Network totals for the dashboard Network zone. Device counts exclude merged/alias
-- devices so they agree with the Devices report. Reporting(24h) = live devices with any
-- fingerprint seen in the last 24h; quiet = the remainder.
WITH live AS (
    SELECT
        d.device_id
      , d.management_status
    FROM
        live_devices d
    )
   , dc   AS (
    SELECT
        count(*) AS total_devices
      , count(*) FILTER (WHERE management_status = 'managed')      AS managed_devices
      , count(*) FILTER (WHERE management_status = 'discovered')   AS discovered_devices
    FROM
        live
    )
   , rep  AS (
    SELECT
        count(DISTINCT l.device_id) AS reporting_24h
    FROM
        live                     l
        JOIN device_fingerprints df
        ON df.device_id = l.device_id
    WHERE
        df.last_seen >= now() -
    INTERVAL '24 hours'
    )
   , sc AS (
SELECT
    COUNT (*) AS services_total
FROM
    services)
  , zones AS (
SELECT
    DISTINCT ZONE
FROM
    agents
WHERE
    ZONE IS NOT NULL
  AND ZONE <> '')
    , zc AS (
SELECT
    COUNT (*) AS distinct_zones
  , string_agg(
    ZONE
  , ', ' ORDER BY ZONE) AS zone_names
FROM
    zones
    )
SELECT
    dc.total_devices
  , dc.managed_devices
  , dc.discovered_devices
  , sc.services_total
  , zc.distinct_zones
  , zc.zone_names
  , rep.reporting_24h
  , (dc.total_devices - rep.reporting_24h) AS quiet_24h
FROM
    dc
  , rep
  , sc
  , zc
