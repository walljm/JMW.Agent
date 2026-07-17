INSERT INTO proj_hardware
(
      device
    , firmware_version
    , updated_at
)
VALUES
    (
          $1
        , $2
        , now()
    ) ON CONFLICT (device) DO
UPDATE set
    firmware_version = COALESCE (proj_hardware.firmware_version, EXCLUDED.firmware_version),
    updated_at = now()
    RETURNING device
