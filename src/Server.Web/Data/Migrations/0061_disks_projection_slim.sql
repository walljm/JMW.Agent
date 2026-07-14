-- Slim proj_disks: name/model/size_bytes/type and the headline SMART fields (health/temp/
-- wear/power-on-hours) stay projected — ListStorageDisks.sql and GetDeviceDisks.sql both read
-- them. These 9 granular SMART counters are read by NEITHER query — pure write cost on every
-- SMART poll for data nobody has ever displayed. Moved to the "Disk SMART Details" fact view.
ALTER TABLE proj_disks
    DROP COLUMN smart_power_cycles,
    DROP COLUMN smart_reallocated_sectors,
    DROP COLUMN smart_uncorrectable_errors,
    DROP COLUMN smart_pending_sectors,
    DROP COLUMN smart_crc_errors,
    DROP COLUMN smart_pct_used,
    DROP COLUMN smart_available_spare_pct,
    DROP COLUMN smart_data_read_gb,
    DROP COLUMN smart_data_written_gb;
