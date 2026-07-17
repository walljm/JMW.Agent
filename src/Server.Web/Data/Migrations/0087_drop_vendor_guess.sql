-- Phase 6a of docs/plans/architecture-identity-facts.md §12: retire proj_devices.vendor_guess.
-- DeviceVendorGuess is now folded into DeviceVendorDerivation as its lowest-priority input
-- (Derived.DeviceVendorCanonical / proj_devices.vendor already includes the inference) rather
-- than being surfaced as a separate column reporting had to COALESCE against. Its ProjectionLibrary
-- column def is removed in the same change.

ALTER TABLE proj_devices
    DROP COLUMN IF EXISTS vendor_guess;
