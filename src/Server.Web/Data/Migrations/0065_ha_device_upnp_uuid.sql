-- Adds the UPnP device UUID column to the Home Assistant device-registry projection.
--
-- HA's "upnp"-flavored connections/identifiers carry a value shaped like
-- "uuid:xxxx-..." (or the fuller USN "uuid:xxxx-...::urn:schemas-upnp-org:device:...") whose
-- UUID(s) match what a network scanner's SSDP probe observes independently for the same
-- device — see HomeAssistantDeviceCollector.ExtractUpnpUuids and DiscoveryMaterializer's
-- MaterializeHomeAssistantDevicesAsync, which promotes each one as its own
-- FingerprintType.Uuid fingerprint (the same type SSDP-scanned devices use) so both
-- observations merge onto one Device row. Pipe-joined when a device carries more than one.
--
-- This is a projection column (not just a fact-view column) because the materializer's
-- identity-resolution query reads it — unlike serial_number/labels, which are purely
-- informational and stay fact-view-only (see FactViewLibrary's "Home Assistant Devices" view).

ALTER TABLE jmwdiscovery.proj_service_ha_devices
    ADD COLUMN IF NOT EXISTS upnp_uuid TEXT;
