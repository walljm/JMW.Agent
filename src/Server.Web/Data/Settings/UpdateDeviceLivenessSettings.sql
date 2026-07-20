UPDATE device_liveness_settings
SET
    window_hours = $1
  , updated_at = now()
WHERE
    id = TRUE RETURNING window_hours
