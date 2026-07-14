-- Recent-activity headline counts. new_devices_7d and not_seen_7d exclude merged/alias
-- devices; not_seen_7d = live devices with NO fingerprint seen in the last 7 days.
WITH live AS (
    SELECT
        d.device_id
      , d.created_at
    FROM
        live_devices d
    )
   , nd   AS (
    SELECT
        count(*) AS new_devices_7d
    FROM
        live
    WHERE
        created_at >= now() -
    INTERVAL '7 days')
-- have_fp / seen count only devices that HAVE fingerprints, so not_seen_7d matches the
-- GetNotSeenDevices list exactly (a device with zero fingerprints appears in neither).
-- One join, one scan of device_fingerprints — FILTER narrows which rows feed the second
-- DISTINCT count without a second join (same idiom as GetDashboardSummary.sql).
   , fp AS (
SELECT
    COUNT (
    DISTINCT l.device_id)                                                     AS have_fp
  , COUNT (
    DISTINCT l.device_id) FILTER (WHERE df.last_seen >= now() - INTERVAL '7 days') AS seen_7d
FROM
    live l
    JOIN device_fingerprints df
ON df.device_id = l.device_id
    )
  , ch AS (
-- Curated count (incidents + change_events), not a raw facts_history diff count — see
-- IncidentQueries.ListRecentActivityAsync, the feed this headline number summarizes.
SELECT
    (
        (SELECT COUNT(*) FROM incidents WHERE coalesce(resolved_at, opened_at) >= now() - INTERVAL '24 hours')
      + (SELECT COUNT(*) FROM change_events WHERE occurred_at >= now() - INTERVAL '24 hours')
    ) AS changes_24h
    )
SELECT
    nd.new_devices_7d
  , (fp.have_fp - fp.seen_7d) AS not_seen_7d
  , ch.changes_24h
FROM
    nd
  , fp
  , ch
