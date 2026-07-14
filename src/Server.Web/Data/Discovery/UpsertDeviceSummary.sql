-- COALESCE-safe upsert of the unified proj_devices summary (vendor, kind). Vendor here is the
-- passive-discovery-promotion path (DiscoveryMaterializer, for devices with no agent) — the
-- agent-direct path (hardware/BACnet/Modbus/Google Wifi collectors reporting their OWN device)
-- reaches proj_devices.vendor via DeviceVendorDerivation instead. Both write the same column;
-- COALESCE means whichever gets there first wins and the other is a safe no-op.
INSERT INTO proj_devices
(
      device
    , vendor
    , kind
    , updated_at
)
VALUES
    (
          $1
        , $2
        , $3
        , now()
    ) ON CONFLICT (device) DO
UPDATE set
    vendor = COALESCE (proj_devices.vendor, EXCLUDED.vendor),
    kind = COALESCE (proj_devices.kind, EXCLUDED.kind),
    updated_at = now()
    RETURNING device
