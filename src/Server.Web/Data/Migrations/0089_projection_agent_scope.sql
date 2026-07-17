-- docs/plans/ha-device-enrichment.md §5: RFC1918 addresses are commonly reused across
-- independent LANs this server ingests from, so an IP→MAC join with no site scope can pair a
-- MAC observed on a completely different network. Adds agent_id to the five projections an
-- IP/MAC join can be scoped against (proj_device_arp, proj_dhcp_leases, proj_dhcp_local_leases,
-- proj_discovered, proj_interfaces) — see ProjectionDef.TracksAgentId. Nullable and nothing
-- backfills it: existing rows read as "unscoped" (unknown agent) until the next poll rewrites
-- them, at which point GenericProjection's normal write path populates it going forward.
ALTER TABLE proj_device_arp
    ADD COLUMN IF NOT EXISTS agent_id UUID;
ALTER TABLE proj_dhcp_leases
    ADD COLUMN IF NOT EXISTS agent_id UUID;
ALTER TABLE proj_dhcp_local_leases
    ADD COLUMN IF NOT EXISTS agent_id UUID;
ALTER TABLE proj_discovered
    ADD COLUMN IF NOT EXISTS agent_id UUID;
ALTER TABLE proj_interfaces
    ADD COLUMN IF NOT EXISTS agent_id UUID;
