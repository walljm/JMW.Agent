-- Move all application tables and views from the default 'public' schema
-- to the 'jmwdiscovery' schema. The schemaversions table (managed by
-- ITPIE.Migrations) stays in public. This migration is idempotent:
-- tables already in jmwdiscovery are silently skipped by the DO block.

CREATE SCHEMA if NOT EXISTS jmwdiscovery;

DO
$$
DECLARE
tbl text;
    tbls
text[] := ARRAY[
        'services', 'service_fingerprints',
        'devices', 'device_fingerprints', 'device_aliases',
        'facts_history',
        'agents', 'collection_targets', 'credentials',
        'users', 'user_sessions',
        'audit_log',
        'excluded_fingerprints',
        'proj_devices', 'proj_systems', 'proj_hardware', 'proj_hardware_inventory',
        'proj_interfaces', 'proj_disks', 'proj_filesystems',
        'proj_docker', 'proj_containers',
        'proj_security', 'proj_batteries', 'proj_updates',
        'proj_processes', 'proj_ports', 'proj_device_services',
        'proj_local_users', 'proj_sessions',
        'proj_device_routes', 'proj_device_arp',
        'proj_device_certs', 'proj_device_trusted_cas',
        'proj_services', 'proj_service_ca', 'proj_service_ca_provisioners', 'proj_service_ca_dns_names',
        'proj_dns_stats', 'proj_dns_zones', 'proj_dns_records',
        'proj_dhcp_scopes', 'proj_dhcp_leases'
    ];
BEGIN
    FOREACH
tbl IN ARRAY tbls LOOP
        IF EXISTS (
            SELECT 1 FROM pg_tables
            WHERE schemaname = 'public' AND tablename = tbl
        ) THEN
            EXECUTE format('ALTER TABLE public.%I SET SCHEMA jmwdiscovery', tbl);
END IF;
END LOOP;
END;
$$;

-- Views must be dropped and recreated after base tables move schemas.
-- Drop first (they reference proj_hardware_inventory which just moved).
DROP VIEW if EXISTS PUBLIC.proj_hw_memory;
DROP VIEW if EXISTS PUBLIC.proj_hw_cpus;
DROP VIEW if EXISTS PUBLIC.proj_hw_fans;
DROP VIEW if EXISTS PUBLIC.proj_hw_psus;
DROP VIEW if EXISTS PUBLIC.proj_hw_transceivers;
DROP VIEW if EXISTS PUBLIC.proj_hw_modules;
DROP VIEW if EXISTS PUBLIC.proj_hw_storage;

-- Recreate views in jmwdiscovery (table references resolve via search_path).
SET
search_path TO jmwdiscovery, PUBLIC;

CREATE
OR REPLACE VIEW proj_hw_memory AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'size_bytes')::bigint   AS size_bytes, (details ->>'speed_mhz')::int       AS speed_mhz, (details ->>'memory_type') AS memory_type
  , (details ->>'form_factor') AS form_factor
  , updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'memory';

CREATE
OR REPLACE VIEW proj_hw_cpus AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , (details ->>'cores')::int           AS cores, (details ->>'threads')::int         AS threads, (details ->>'speed_mhz')::int       AS speed_mhz, (details ->>'socket') AS socket
  , updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'cpu';

CREATE
OR REPLACE VIEW proj_hw_fans AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'rpm')::int               AS rpm, (details ->>'rpm_low_threshold')::int  AS rpm_low_threshold, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'fan';

CREATE
OR REPLACE VIEW proj_hw_psus AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'capacity_watts')::int     AS capacity_watts, (details ->>'input_voltage')::numeric  AS input_voltage, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'psu';

CREATE
OR REPLACE VIEW proj_hw_transceivers AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'wavelength_nm')::int       AS wavelength_nm, (details ->>'tx_power_dbm')::numeric    AS tx_power_dbm, (details ->>'rx_power_dbm')::numeric    AS rx_power_dbm, (details ->>'temperature_c')::numeric   AS temperature_c, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'transceiver';

CREATE
OR REPLACE VIEW proj_hw_modules AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , firmware
  , status
  , is_fru
  , (details ->>'port_count')::int          AS port_count, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'module';

CREATE
OR REPLACE VIEW proj_hw_storage AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , firmware
  , status
  , is_fru
  , (details ->>'size_bytes')::bigint        AS size_bytes, (details ->>'type') AS type
  , (details ->>'smart_health') AS smart_health
  , (details ->>'smart_temp_c')::numeric     AS smart_temp_c, (details ->>'smart_wear_pct')::numeric   AS smart_wear_pct, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'storage';

-- Set the default search_path for the application role so tools that
-- connect without an explicit Search Path still find the right schema.
-- Guarded: the jmwfacts login role only exists in deployed environments, so skip
-- this when it is absent (e.g. integration tests apply the migrations under a
-- different role). The connection string's Search Path still applies regardless.
DO
$$
BEGIN
    IF
EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'jmwfacts') THEN
        ALTER
ROLE jmwfacts SET search_path TO jmwdiscovery, PUBLIC;
END IF;
END
$$;

