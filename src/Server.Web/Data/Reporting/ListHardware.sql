-- JOIN proj_devices is safe (never drops rows): every device has a proj_devices row
-- from creation (DeviceRegistry.CreateDeviceAsync + the context engine's backfill).
--
-- Vendor display: the device's own DMI/SMBIOS vendor first, else the unified device vendor
-- (proj_devices.vendor, which already includes the inferred guess as its lowest-priority fan-in
-- input — see DeviceVendorDerivation). proj_hardware.system_vendor is reserved for the
-- agent/dmidecode path, so a discovered/derived vendor (e.g. model→vendor for a Google Wifi
-- station) lands only in proj_devices — coalescing here surfaces it on this page.
SELECT
    h.device
  , s.hostname
  , COALESCE(NULLIF(h.system_vendor, ''), NULLIF(pd.vendor, '')) AS system_vendor
  , h.system_model
  , h.system_serial
  , h.bios_version
  , h.virtualization
  , h.cpu_model
  , h.cpu_vendor
  , h.cpu_cores
  , h.cpu_logical_cores
  , h.cpu_mhz
  , h.total_mem_bytes
  , disks.total_bytes::bigint AS total_storage_bytes
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_hardware h
    LEFT JOIN proj_systems s
    ON s.device = h.device
    JOIN proj_devices pd
    ON pd.device = h.device
    LEFT JOIN LATERAL (
        SELECT SUM(d.size_bytes) AS total_bytes FROM proj_disks d WHERE d.device = h.device
    ) disks ON TRUE
WHERE
      (
          $1::text IS NULL
     OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
     OR COALESCE(NULLIF(h.system_vendor, ''), NULLIF(pd.vendor, '')) ILIKE '%' || $1 || '%'
     OR COALESCE(h.system_model, '') ILIKE '%' || $1 || '%'
     OR COALESCE(h.cpu_model, '') ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
          -- pd.device is the uniform tiebreaker: it is join-equal to h.device, and the planner's
          -- equivalence classes let either alias match whichever table's expression index serves
          -- the sort (the hostname sort key lives on pd, cpu on h — verified via EXPLAIN).
     OR (__SORT_KEY__, pd.device) __CMP__ ($2, $3)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , pd.device __DIR__
LIMIT $4
