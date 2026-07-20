SELECT
    d.device_id
  , s.hostname
  , s.os_family
    -- Already includes the inferred guess (VendorOsFromDeviceBannerDerivation) as
    -- SystemOsDistroDerivation's lowest-priority fan-in input.
  , s.os_distro AS os_distro
  , d.management_status
    -- Newest fingerprint sighting: stamped on every resolution, so it reflects true recency for
    -- every device (managed included) and matches the liveness-window filter. proj_systems.updated_at
    -- is deliberately NOT used — it only moves on a data change and goes stale on a live static host.
  , (SELECT max(df.last_seen) FROM device_fingerprints df WHERE df.device_id = d.device_id) AS last_seen
    -- Already includes the inferred guess (VendorFromOsDistroDerivation et al.) as
    -- DeviceVendorDerivation's lowest-priority fan-in input.
  , pd.vendor AS vendor
FROM
    devices                d
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
LEFT JOIN proj_devices  pd
ON pd.device = d.device_id::text
WHERE
    EXISTS (
    SELECT 1 FROM visible_devices ld WHERE ld.device_id = d.device_id
    )
  AND (
    $1::text IS NULL
   OR d.management_status = $1)
  AND (
    $2::text IS NULL
   OR (
    COALESCE (
    s.hostname
    , '')
    , d.device_id::text)
    > (
    $2
    , $3))
  AND (
    $4::text IS NULL
   OR COALESCE (
    s.hostname
    , '') ILIKE '%' || $4 || '%'
   OR EXISTS (
    SELECT
    1
    FROM
    device_fingerprints df
    WHERE
    df.device_id = d.device_id
  AND df.fp_value ILIKE '%' || $4 || '%'))
ORDER BY
    COALESCE (
    s.hostname
  , '')               ASC
  , d.device_id::text ASC
    LIMIT $5
