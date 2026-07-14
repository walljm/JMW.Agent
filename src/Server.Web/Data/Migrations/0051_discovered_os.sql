-- Agent-side HTTP identity resolution (Recog matching) yields an OS family/product string per
-- discovered neighbor (e.g. "Linux", "RouterOS"). Store it on proj_discovered so the materializer
-- can promote it to the canonical proj_systems.os_family, making OS queryable across devices.
-- Device-collected OS (from a configured collector) still wins via COALESCE in UpsertDeviceSystem.
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN IF NOT EXISTS os text;
