-- proj_discovered_nbns_names (added 0048, extended 0052) was write-only: nothing in the
-- codebase ever read it. NBNS name-table data now renders directly from facts_history via
-- the "Discovered: NBNS Names" fact view (FactViewLibrary.cs), so the projection, its
-- retention policy row (0069), and its device-merge cascade entry (DeviceRegistry.cs) are
-- dead weight. Drop it.

DELETE FROM retention_policies WHERE table_name = 'proj_discovered_nbns_names';

DROP TABLE IF EXISTS jmwdiscovery.proj_discovered_nbns_names;
