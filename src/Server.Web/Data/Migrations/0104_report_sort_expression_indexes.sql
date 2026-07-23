-- Report list endpoints (DeviceListApi/PortsApi/ContainersApi/ArpApi/ComponentsApi/InterfacesApi/
-- HardwareApi/AgentsApi) build their ORDER BY / keyset WHERE tuple from a per-column
-- SortExpressions allowlist (see each file). Several of those expressions wrap a nullable column in
-- coalesce()/lpad() purely so a NULL value stays reachable in keyset pagination — but the existing
-- indexes on these tables were built on the RAW column, not the wrapped expression, so Postgres
-- can't use them to satisfy the ORDER BY: it has to sort the whole filtered result to return one
-- page. coalesce/lpad are IMMUTABLE built-ins, so an expression index on the exact wrapped form
-- fixes this with no application change.
--
-- Scope: only sort columns that live on each query's own driving/FROM table are covered here.
-- Verified via EXPLAIN against a synthetic repro: a LEFT-JOINed (non-driving) column's index is
-- never used to satisfy ORDER BY across the join (Postgres falls back to Hash Left Join + full
-- Sort regardless), so proj_systems.hostname (joined into ComponentsApi/InterfacesApi/HardwareApi)
-- and DeviceListApi's cross-table/lateral-pick columns are deliberately NOT indexed here — they
-- need the sort value denormalized onto the driving table first, tracked separately.

-- ContainersApi "state" sort — proj_containers is the driving table.
CREATE INDEX IF NOT EXISTS proj_containers_state_sort_idx
    ON proj_containers ((coalesce(state, '')), device, container);

-- ArpApi "mac" sort — proj_device_arp is the driving table.
CREATE INDEX IF NOT EXISTS proj_device_arp_mac_sort_idx
    ON proj_device_arp ((coalesce(mac, '')), device, arp);

-- ArpApi default "ip" sort orders by (arp, device); the existing PK is (device, arp) — wrong
-- leading column for this sort.
CREATE INDEX IF NOT EXISTS proj_device_arp_arp_device_idx
    ON proj_device_arp (arp, device);

-- HardwareApi "cpu" sort — proj_hardware is the driving table; tiebreak is device alone.
CREATE INDEX IF NOT EXISTS proj_hardware_cpu_model_sort_idx
    ON proj_hardware ((coalesce(cpu_model, '')), device);

-- ComponentsApi "class" sort — proj_hardware_inventory is the driving table.
CREATE INDEX IF NOT EXISTS proj_hardware_inventory_class_sort_idx
    ON proj_hardware_inventory ((coalesce(class, '')), device, hwcomponent);

-- InterfacesApi "speed" sort — proj_interfaces is the driving table.
CREATE INDEX IF NOT EXISTS proj_interfaces_speed_sort_idx
    ON proj_interfaces ((coalesce(speed_bps, -1)), device, interface);

-- PortsApi "port" sort — proj_ports is the driving table. proj_ports_port_idx (raw port, partial)
-- stays: it serves the p.port = $1 equality filter, which this expression form can't.
CREATE INDEX IF NOT EXISTS proj_ports_port_sort_idx
    ON proj_ports ((lpad(port::text, 5, '0')), device, listeningport);

-- AgentsApi "status" sort — agents is the driving (only) table; agents_status_idx is a plain
-- single-column index built for the status = $1 equality filter, not this sort+tiebreak tuple.
CREATE INDEX IF NOT EXISTS agents_status_sort_idx
    ON agents (status, created_at);
