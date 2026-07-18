-- docs/plans/l3-topology.md Track 1: authoritative host-local subnet detection. The agent's
-- DockerCollector now enumerates GET /v1.43/networks; one row per IPAM subnet. SubnetsApi joins
-- (device, cidr) here to tell a host-local NAT bridge (driver=bridge — Docker's 172.17.0.0/16
-- et al., present on every host but non-routable between them) apart from a routable
-- macvlan/ipvlan/overlay, and keys host-local subnets per-host instead of merging identical
-- CIDRs across hosts into one bogus shared node. Base table mirrors ProjectionSchema.GenerateDdl
-- for the proj_docker_networks def; additive columns stay owned by that generator.
-- Key = subnet CIDR (e.g. "172.17.0.0/16"). Not agent-scoped: the join is by device id.
CREATE TABLE if NOT EXISTS proj_docker_networks (
    device TEXT NOT NULL,
    dockernet TEXT NOT NULL, -- subnet CIDR
    name TEXT,
    driver TEXT,
    scope TEXT,
    bridge_name TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, dockernet)
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
    'proj_docker_networks'
    , 'network'
    , INTERVAL '7 days'
    , 'Docker network snapshot; stale entries mislead L3 host-local subnet classification'
)
ON CONFLICT (table_name) DO NOTHING;
