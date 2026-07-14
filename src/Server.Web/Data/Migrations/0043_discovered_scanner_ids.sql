-- T2-2b: scanner identity ids promoted to fingerprints. New proj_discovered columns hold the
-- Philips Hue bridge id and the ONVIF hardware id; the materializer's scanner-id pass reads them
-- and resolves each as a ChassisSerial fingerprint (unioned with the row's MAC) so a device seen
-- by one observer via its id and by another via its MAC converges onto one record.
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN if NOT EXISTS hue_bridge_id text,
    ADD COLUMN IF NOT EXISTS onvif_hardware_id text;
