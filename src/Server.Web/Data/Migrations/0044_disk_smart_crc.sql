-- T2-6: the one SMART failure-predictor the collector wasn't emitting yet — UDMA CRC error
-- count (SMART id 199), a cable/link-integrity signal. Cross-device queryable, so a proj_disks
-- column (the rest of the SMART attribute set already lives there).
ALTER TABLE jmwdiscovery.proj_disks
    ADD COLUMN if NOT EXISTS smart_crc_errors bigint;
