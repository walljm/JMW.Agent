-- proj_devices.vendor_guess: inferred vendor from a curated, vendor-exclusive signature
-- (OS distro today, more signatures land per docs/plans/vendor-derivation-updates.md §2).
-- Kept as its own column, not merged into `vendor` — that column is a fan-in over protocols
-- that self-report vendor directly (DeviceVendorDerivation); this one is an inference from a
-- proxy signal and stays separate so the distinction is auditable. Reporting queries should
-- only fall back to this column when `vendor` is NULL.
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS vendor_guess TEXT;
