-- Phase 6b of docs/plans/architecture-identity-facts.md §12: retire proj_systems.os_distro_guess.
-- DeviceOsGuess is now folded into the new SystemOsDistroDerivation as its lowest-priority input,
-- alongside the raw, device-reported Device[].OS.Distro fact — proj_systems.os_distro now maps to
-- that canonical fan-in output (Derived.SystemOsDistroCanonical) rather than the raw path directly,
-- and already includes the inference. Its ProjectionLibrary column def is removed in the same
-- change.

ALTER TABLE proj_systems
    DROP COLUMN IF EXISTS os_distro_guess;
