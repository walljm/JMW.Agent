-- Phase 3 of docs/plans/architecture-identity-facts.md §7: retire the eleven materializer-only
-- identity-signal columns from proj_discovered now that every read has moved to
-- materialization_facts (Phase 2a-2f) and the dual write has been populating it since Phase 1
-- (migration 0085 also backfilled the pre-existing values). Dropping these ends the dual write:
-- their ProjectionLibrary column defs are removed in the same change, so GenericProjection no
-- longer tracks them and IdentityFactProjection is the sole writer.
--
-- mac/obscured_mac/hostname/friendly_name/sources/vendor/model stay — reports join/filter/
-- trust-guard on them (architecture-identity-facts.md §2), so they remain wide columns.

ALTER TABLE proj_discovered
    DROP COLUMN IF EXISTS onvif_serial,
    DROP COLUMN IF EXISTS roku_serial,
    DROP COLUMN IF EXISTS snmp_serial,
    DROP COLUMN IF EXISTS ssdp_uuid,
    DROP COLUMN IF EXISTS wsd_uuid,
    DROP COLUMN IF EXISTS hue_bridge_id,
    DROP COLUMN IF EXISTS onvif_hardware_id,
    DROP COLUMN IF EXISTS cast_id,
    DROP COLUMN IF EXISTS device_type,
    DROP COLUMN IF EXISTS os,
    DROP COLUMN IF EXISTS ssh_host_key;
