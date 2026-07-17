-- docs/plans/ha-device-enrichment.md §6: promotes an HA device's sw_version (device-registry
-- native, or the update.*_firmware fallback — see HomeAssistantCollector.AddHealthFacts) onto
-- the resolved device. proj_hardware has vendor/model/serial but no generic firmware/software
-- version column; this adds one, fill-only (same COALESCE semantics as the existing columns).
ALTER TABLE proj_hardware
    ADD COLUMN IF NOT EXISTS firmware_version TEXT;
