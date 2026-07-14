-- Partial indexes supporting DiscoveryMaterializer's promotion-gap pass (re-promotes
-- proj_discovered vendor/model/os/hostname onto proj_hardware/proj_systems for devices
-- that were fingerprinted before a scanner identified them — not just on first mint).
-- These let Postgres jump straight to the (normally small, self-limiting) set of
-- already-registered devices that still have a gap, instead of scanning every row.
-- They do not help find devices with NO proj_hardware/proj_systems row at all (agentless
-- discovered devices) — that case is bounded by the device_fingerprints scan instead,
-- which is already served by its (fp_type, fp_value) primary key.
CREATE INDEX IF NOT EXISTS ix_proj_hardware_gap
    ON proj_hardware (device)
    WHERE system_vendor IS NULL OR system_model IS NULL;

CREATE INDEX IF NOT EXISTS ix_proj_systems_gap
    ON proj_systems (device)
    WHERE os_family IS NULL OR hostname IS NULL;
