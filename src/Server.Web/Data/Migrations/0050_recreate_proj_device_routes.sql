-- Reinstates proj_device_routes, dropped in 0028 as write-only. It now has a real reader: the
-- Subnets page (SubnetsApi) uses connected/static routes to fill subnet-membership gaps that
-- single-valued proj_interfaces.ipv4 can miss (an interface with a secondary IP in another
-- subnet only keeps its last-written address), and default-route gateways to detect edges that
-- lead off the observed network entirely (the Subnets L3 graph's "Internet" node). Same schema
-- as the original table.
-- Key = destination CIDR (e.g. "0.0.0.0/0", "192.168.1.0/24", "::/0").
CREATE TABLE if NOT EXISTS proj_device_routes (
    device TEXT NOT NULL,
    route TEXT NOT NULL, -- destination CIDR
    family TEXT,
    gateway TEXT,
    iface TEXT,
    metric INTEGER,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, route)
);

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_device_routes'
    , 'network'
    , INTERVAL '7 days'
    , 'Routing table snapshot; stale entries mislead topology queries'
)
ON CONFLICT (table_name) DO NOTHING;
