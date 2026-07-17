-- Live-device counts by device-maker vendor. Vendor is derived exactly as on the Devices
-- report — the unified cross-protocol field (DeviceVendorDerivation fans in hardware/BACnet/
-- Modbus/Google Wifi, plus the inferred guess as its lowest-priority input), falling back to
-- proj_hardware.system_vendor for devices whose derivation hasn't re-run since this field was
-- added. NULL vendors group together and are labelled by the caller. Excludes merged/alias
-- devices.
SELECT
    coalesce(pdv.vendor, hw.system_vendor) AS vendor
  , count(*) AS COUNT
FROM
    live_devices d
    LEFT JOIN proj_hardware hw
ON hw.device = d.device_id::text
    LEFT JOIN proj_devices pdv ON pdv.device = d.device_id::text
GROUP BY
    COALESCE (pdv.vendor, hw.system_vendor)
ORDER BY
    COUNT  DESC
  , vendor ASC
