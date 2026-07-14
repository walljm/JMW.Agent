-- proj_discovered_coap_resources / proj_discovered_coap_formats (added 0048) were write-only,
-- same as proj_discovered_nbns_names (see 0075): nothing in the codebase ever read them. CoAP
-- resource/content-format data now renders directly from facts_history via the "Discovered:
-- CoAP Resources" / "Discovered: CoAP Content Formats" fact views (FactViewLibrary.cs). Drop
-- the projections, their retention policy rows (0069), and their device-merge cascade entries
-- (DeviceRegistry.cs).

DELETE FROM retention_policies WHERE table_name IN ('proj_discovered_coap_resources', 'proj_discovered_coap_formats');

DROP TABLE IF EXISTS jmwdiscovery.proj_discovered_coap_resources;
DROP TABLE IF EXISTS jmwdiscovery.proj_discovered_coap_formats;
