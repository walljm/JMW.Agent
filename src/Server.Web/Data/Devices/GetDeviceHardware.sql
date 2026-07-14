SELECT
    cpu_model
  , cpu_vendor
  , cpu_cores
  , cpu_logical_cores
  , cpu_mhz
  , total_mem_bytes
  , system_vendor
  , system_model
  , system_serial
  , bios_version
  , virtualization
  , updated_at
FROM
    proj_hardware
WHERE
    device = $1
