-- PERF-012: GIN trigram indexes to support leading-wildcard ILIKE search on ARP/MAC columns.
-- ListArp.sql uses `a.mac ILIKE '%' || $1 || '%'` and `a.arp ILIKE '%' || $1 || '%'`.
-- B-tree indexes cannot satisfy a leading wildcard; trigram indexes can.

CREATE
EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX if NOT EXISTS ix_proj_device_arp_mac_trgm
    ON proj_device_arp USING GIN (mac gin_trgm_ops);

CREATE INDEX if NOT EXISTS ix_proj_device_arp_arp_trgm
    ON proj_device_arp USING GIN (arp gin_trgm_ops);
