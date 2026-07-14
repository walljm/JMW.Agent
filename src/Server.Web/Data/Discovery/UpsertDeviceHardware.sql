INSERT INTO proj_hardware
(
      device
    , system_vendor
    , system_model
    , system_serial
    , updated_at
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , now()
    ) ON CONFLICT (device) DO
UPDATE set
    system_vendor = COALESCE (proj_hardware.system_vendor, EXCLUDED.system_vendor),
    system_model = COALESCE (proj_hardware.system_model, EXCLUDED.system_model),
    system_serial = COALESCE (proj_hardware.system_serial, EXCLUDED.system_serial),
    updated_at = now()
    RETURNING device
