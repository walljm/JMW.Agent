-- proj_service_ha_devices (added 0064, extended 0065) is no longer read by anything: HA device
-- promotion now resolves inline from the ingest batch's own facts (HomeAssistantDevicePromotion)
-- instead of a DiscoveryMaterializer pass rereading this table, and the "N devices" count it fed
-- into ListServices.sql was never accurate anyway (it counted rows the agent chose to emit after
-- its own registry filtering, not the true registry size or the number of entries that actually
-- resolved to a device). Every field it carried is already visible via the "Home Assistant
-- Devices" fact view (FactViewLibrary.cs), which reads facts_history directly. Drop the
-- projection and its retention policy row (0069).

DELETE FROM retention_policies WHERE table_name = 'proj_service_ha_devices';

DROP TABLE IF EXISTS jmwdiscovery.proj_service_ha_devices;
