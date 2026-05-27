-- 0023 drop legacy device tables: entity model is the sole data store.
-- Runs after 0022_backfill_devices_to_entities, which preserves user notes
-- and the device kind classification before these tables disappear.

DROP TABLE IF EXISTS device_networks;
DROP TABLE IF EXISTS device_sightings;
DROP TABLE IF EXISTS device_hostnames;
DROP TABLE IF EXISTS devices;
