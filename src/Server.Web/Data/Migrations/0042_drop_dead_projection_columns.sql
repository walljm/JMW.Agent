-- §4 dead-weight removal (data-loss-inventory.md): drop projection columns that are written
-- on every ingest but read by NO query. Verified reader-free via a grep audit; each column's
-- fact path now routes to a fact view (FV-16..FV-20) or is RawOnly, so no data is lost — only
-- the redundant projection write is removed. The tables themselves stay.
--
-- CASCADE because a couple of these columns carried a projection-side index (e.g. a GIN index
-- on the jsonb details blob); dropping the column drops its own dependent index. Safe here: no
-- query reads these columns, so nothing else depends on them.
--
-- NOT dropped (the audit doc was wrong — these DO have readers / are infrastructure):
--   proj_dhcp_leases.expires_at   (read by ListDhcpLeases.sql)
--   proj_discovered.rx_bytes/tx_bytes (sighting telemetry, read by GetDeviceSightings.sql)
--   proj_filesystems.total_bytes  (displayed; the dead one is proj_interfaces.total_bytes)
--   proj_devices.updated_at       (GenericProjection writes updated_at to every table)

ALTER TABLE jmwdiscovery.proj_interfaces
DROP
COLUMN IF EXISTS alias CASCADE,
    DROP
COLUMN IF EXISTS admin_status CASCADE,
    DROP
COLUMN IF EXISTS oper_status CASCADE,
    DROP
COLUMN IF EXISTS rx_bytes CASCADE,
    DROP
COLUMN IF EXISTS tx_bytes CASCADE,
    DROP
COLUMN IF EXISTS rx_packets CASCADE,
    DROP
COLUMN IF EXISTS tx_packets CASCADE,
    DROP
COLUMN IF EXISTS total_bytes CASCADE;

ALTER TABLE jmwdiscovery.proj_containers
DROP
COLUMN IF EXISTS net_rx_bytes CASCADE,
    DROP
COLUMN IF EXISTS net_tx_bytes CASCADE;

ALTER TABLE jmwdiscovery.proj_service_ca_provisioners
DROP
COLUMN IF EXISTS min_duration CASCADE,
    DROP
COLUMN IF EXISTS max_duration CASCADE;

ALTER TABLE jmwdiscovery.proj_dhcp_leases
DROP
COLUMN IF EXISTS lease_type CASCADE;

ALTER TABLE jmwdiscovery.proj_hardware_inventory
DROP
COLUMN IF EXISTS details CASCADE;

ALTER TABLE jmwdiscovery.proj_discovered
DROP
COLUMN IF EXISTS tls_cn CASCADE,
    DROP
COLUMN IF EXISTS http_title CASCADE;
