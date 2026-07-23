-- Context derivation "identity-friendly-name" (docs/plans/context-derivations.md §4): the
-- display-name rollup per device, in priority order: an operator-set/promoted friendly name
-- (proj_systems.friendly_name), else the best friendly-name-ish value any observer recorded for
-- this device's newest MAC in proj_discovered (agentless devices not yet promoted), else the
-- real hostname. Set form of DeviceListApi's former disc lateral — guards kept verbatim:
-- pd.obscured_mac IS NULL excludes Google Wifi rows whose mac came from obscured-MAC
-- reconstruction (a stale mDNS advertisement can reconstruct to a different device's MAC; that
-- name belongs to the cast-id identity, not this one).
WITH newest_mac AS (
    SELECT DISTINCT ON (device_id)
        device_id
      , fp_value
    FROM
        device_fingerprints
    WHERE
        fp_type = 'mac'
    ORDER BY
        device_id
      , last_seen DESC
    )
  , disc AS (
    SELECT DISTINCT ON (nm.device_id)
        nm.device_id
      , COALESCE(pd.friendly_name, pd.hostname) AS name
    FROM
        newest_mac           nm
        JOIN proj_discovered pd
        ON pd.mac = nm.fp_value
    WHERE
          pd.obscured_mac IS NULL
      AND COALESCE(pd.friendly_name, pd.hostname) IS NOT NULL
    ORDER BY
        nm.device_id
      , pd.updated_at DESC
    )
SELECT
    d.device_id::text AS device
  , COALESCE(s.friendly_name, disc.name, s.hostname) AS value
FROM
    devices                d
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
    LEFT JOIN disc
    ON disc.device_id = d.device_id
WHERE
    COALESCE(s.friendly_name, disc.name, s.hostname) IS NOT NULL
