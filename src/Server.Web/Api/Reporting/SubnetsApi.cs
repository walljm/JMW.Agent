using System.Net;

using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Synthesizes L3 subnets at query time from existing projections — no dedicated
/// <c>proj_subnets</c> table (see scratch/topology.md §9: start with query-time synthesis,
/// materialize only if indexing/perf later demands it). Sources: <c>proj_interfaces</c>
/// (agent-observed CIDRs), <c>proj_dhcp_scopes</c> (name/range/gateway), <c>proj_device_routes</c>
/// (connected/static routes fill subnet-membership gaps a single-valued interface IP can miss;
/// default-route gateways fill a subnet's gateway when DHCP doesn't provide one and feed the L3
/// graph's "Internet" edge), and a host-IP pool (interfaces + ARP + DHCP leases + discovered) for
/// per-subnet host counts.
/// </summary>
public static class SubnetsApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/subnets", ListSubnets)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static async Task<IResult> ListSubnets(NpgsqlDataSource db, string? q, CancellationToken ct)
    {
        List<SubnetListItem> items = await QueryAsync(db, q, ct);
        return Results.Ok(new { items });
    }

    public static async Task<List<SubnetListItem>> QueryAsync(
        NpgsqlDataSource db,
        string? search,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<SubnetAggregate> subnets = await BuildAggregatesAsync(conn, ct);
        Dictionary<string, SubnetInterface> interfaceByIp = BuildInterfaceIpIndex(subnets);

        IEnumerable<SubnetAggregate> filtered = string.IsNullOrWhiteSpace(search)
            ? subnets
            : subnets.Where(
                s => s.Network.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                  || (s.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                  || (s.Gateway?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            );

        List<SubnetAggregate> ordered = filtered
            .OrderBy(s => s.Network.BaseAddress, IpAddressComparer.Instance)
            .ThenBy(s => s.Network.PrefixLength)
            .ToList();

        List<SubnetListItem> items = new(ordered.Count);
        foreach (SubnetAggregate agg in ordered)
        {
            (string? gwDeviceId, string? gwHostname) =
                await ResolveGatewayAsync(conn, agg.Gateway, interfaceByIp, ct);
            items.Add(
                new SubnetListItem(
                    agg.Network.ToString(),
                    // For a host-local Docker bridge the DHCP-scope Name is always absent; surface
                    // the Docker network name ("bridge"/"mynet") there instead so the row is legible.
                    agg.Name ?? agg.DockerNetName,
                    agg.Gateway,
                    gwDeviceId,
                    gwHostname,
                    agg.HostIps.Count,
                    agg.Sources.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    agg.OwnerHost,
                    agg.HostLocalDevice is not null
                )
            );
        }

        return items;
    }

    /// <summary>Finds one subnet by its canonical network for the detail page. Null when not found.</summary>
    public static async Task<SubnetDetail?> GetDetailAsync(
        NpgsqlDataSource db,
        IPNetwork network,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<SubnetAggregate> subnets = await BuildAggregatesAsync(conn, ct);

        SubnetAggregate? agg = subnets.Find(s => s.Network.Equals(network));
        if (agg is null)
        {
            return null;
        }

        Dictionary<string, SubnetInterface> interfaceByIp = BuildInterfaceIpIndex(subnets);
        (string? gwDeviceId, string? gwHostname) = await ResolveGatewayAsync(conn, agg.Gateway, interfaceByIp, ct);

        return new SubnetDetail(
            agg.Network.ToString(),
            agg.Name,
            agg.Gateway,
            gwDeviceId,
            gwHostname,
            agg.DhcpService,
            agg.DhcpStart,
            agg.DhcpEnd,
            agg.HostIps.Count,
            agg.Sources.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            agg.Interfaces
                .OrderBy(i => i.Device, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Ip, StringComparer.Ordinal)
                .ToArray()
        );
    }

    /// <summary>
    /// Builds the L3 topology graph for the Subnets page: one node per subnet, one node per
    /// router (a subnet's resolved gateway, or any device with interfaces in ≥2 subnets), and an
    /// "Internet" node only when a device's default-route gateway leads somewhere outside every
    /// known subnet — the one thing route data uniquely tells us that ARP/interfaces/DHCP can't.
    /// No fabricated hierarchy beyond that: honest degradation over a guessed tree shape.
    /// </summary>
    public static async Task<SubnetGraph> GetGraphAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<SubnetAggregate> subnets = await BuildAggregatesAsync(conn, ct);
        if (subnets.Count == 0)
        {
            return new SubnetGraph([], []);
        }

        Dictionary<string, SubnetInterface> interfaceByIp = BuildInterfaceIpIndex(subnets);
        List<SubnetGraphNode> nodes = [];
        List<SubnetGraphEdge> edges = [];
        HashSet<(string From, string To)> drawnEdges = [];
        Dictionary<string, string> routerIdByKey = new(StringComparer.Ordinal);
        int subnetSeq = 0;
        int routerSeq = 0;
        string? internetId = null;

        void AddEdge(string fromId, string toId, string? via = null)
        {
            if (drawnEdges.Add((fromId, toId)))
            {
                edges.Add(new SubnetGraphEdge(fromId, toId, via));
            }
        }

        string GetOrCreateRouter(string key, string? label)
        {
            if (routerIdByKey.TryGetValue(key, out string? existingId))
            {
                return existingId;
            }

            string id = "r" + routerSeq++;
            routerIdByKey[key] = id;
            nodes.Add(new SubnetGraphNode(id, SanitizeLabel(label ?? key), "router"));
            return id;
        }

        // Subnet nodes, ordered the same way the list page shows them.
        Dictionary<SubnetAggregate, string> subnetNodeId = new();
        foreach (SubnetAggregate agg in subnets
            .OrderBy(s => s.Network.BaseAddress, IpAddressComparer.Instance)
            .ThenBy(s => s.Network.PrefixLength))
        {
            string id = "n" + subnetSeq++;
            subnetNodeId[agg] = id;
            string label;
            if (agg.OwnerHost is { } host)
            {
                // Host-local Docker bridge: label with the owning host so two hosts' identical
                // 172.17.0.0/16 nodes read as distinct, not one shared (routable) subnet.
                string net = agg.DockerNetName is { Length: > 0 } dn ? dn : "docker";
                label = $"{agg.Network} · {net}@{host}";
            }
            else
            {
                label = agg.Network + (agg.Name is { Length: > 0 } n ? " · " + n : "")
                      + $" · {agg.HostIps.Count} hosts";
            }

            nodes.Add(new SubnetGraphNode(id, SanitizeLabel(label), "subnet"));
        }

        // Router --> Subnet edges from each subnet's resolved gateway.
        foreach (SubnetAggregate agg in subnets)
        {
            if (string.IsNullOrWhiteSpace(agg.Gateway))
            {
                continue;
            }

            (string? gwDeviceId, string? gwHostname) =
                await ResolveGatewayAsync(conn, agg.Gateway, interfaceByIp, ct);
            string key = gwDeviceId is { Length: > 0 } d ? "dev:" + d : "ip:" + agg.Gateway;
            string routerId = GetOrCreateRouter(key, gwHostname ?? agg.Gateway);
            // Track 3: label the edge with the router's own interface facing this subnet, when known.
            string? via = interfaceByIp.TryGetValue(agg.Gateway, out SubnetInterface? gwIf)
                ? gwIf.InterfaceName
                : null;
            AddEdge(routerId, subnetNodeId[agg], via);
        }

        // Router --> Subnet edges from devices whose interfaces span ≥2 subnets — a router even
        // when it isn't resolved as anyone's DHCP/ARP gateway (e.g. a second, unmanaged NIC).
        Dictionary<string, List<SubnetAggregate>> deviceSubnets = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> deviceHostname = new(StringComparer.OrdinalIgnoreCase);
        foreach (SubnetAggregate agg in subnets)
        {
            foreach (SubnetInterface iface in agg.Interfaces)
            {
                if (!deviceSubnets.TryGetValue(iface.Device, out List<SubnetAggregate>? owned))
                {
                    owned = [];
                    deviceSubnets[iface.Device] = owned;
                }

                if (!owned.Contains(agg))
                {
                    owned.Add(agg);
                }

                deviceHostname[iface.Device] = iface.Hostname ?? deviceHostname.GetValueOrDefault(iface.Device);
            }
        }

        foreach ((string device, List<SubnetAggregate> owned) in deviceSubnets)
        {
            if (owned.Count < 2)
            {
                continue;
            }

            string routerId = GetOrCreateRouter("dev:" + device, deviceHostname.GetValueOrDefault(device) ?? device);
            foreach (SubnetAggregate agg in owned)
            {
                // Track 3: label with this device's interface into that subnet.
                string? via = agg.Interfaces
                    .FirstOrDefault(i => string.Equals(i.Device, device, StringComparison.OrdinalIgnoreCase))?
                    .InterfaceName;
                AddEdge(routerId, subnetNodeId[agg], via);
            }
        }

        // Track 2 — VPN/overlay clouds: a subnet reached over a tailscale/wireguard/openvpn/
        // zerotier interface egresses to an overlay network. Draw one cloud per overlay kind on
        // the far side of the subnet, revealing which hosts are on the tailnet/VPN. Evidence-only
        // (interface name; Tailscale additionally confirmed by its 100.64.0.0/10 CGNAT range) —
        // the same honest simplification the Internet node already makes, never fabricated.
        Dictionary<string, string> vpnIdByLabel = new(StringComparer.Ordinal);
        foreach (SubnetAggregate agg in subnets)
        {
            string? overlay = OverlayLabel(agg);
            if (overlay is null)
            {
                continue;
            }

            if (!vpnIdByLabel.TryGetValue(overlay, out string? vpnId))
            {
                vpnId = "vpn" + vpnIdByLabel.Count;
                vpnIdByLabel[overlay] = vpnId;
                nodes.Add(new SubnetGraphNode(vpnId, overlay, "vpn"));
            }

            AddEdge(subnetNodeId[agg], vpnId);
        }

        // Internet --> Router: only when a default-route gateway leads outside every known
        // subnet. If every agent's default route resolves to a subnet we already model (the
        // common single-router home case), there is nothing to draw here — that's truthful, not
        // a gap: it means every router we can see is fully accounted for by the edges above.
        await foreach ((string device, string? hostname, string? gateway) in conn.ListDefaultRoutesAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(gateway)
             || !IPAddress.TryParse(gateway, out IPAddress? gwAddr)
             || subnets.Any(a => a.Network.Contains(gwAddr)))
            {
                continue;
            }

            internetId ??= CreateInternetNode(nodes);
            string routerId = GetOrCreateRouter("dev:" + device, hostname ?? device);
            AddEdge(internetId, routerId);
        }

        return new SubnetGraph(nodes, edges);
    }

    private static string CreateInternetNode(List<SubnetGraphNode> nodes)
    {
        const string id = "internet";
        nodes.Add(new SubnetGraphNode(id, "Internet", "internet"));
        return id;
    }

    private static readonly IPNetwork TailscaleCgnat = IPNetwork.Parse("100.64.0.0/10");

    /// <summary>
    /// Classifies a subnet as an overlay/VPN by its attaching interface name, returning the
    /// cloud label to draw (or null for an ordinary subnet). Tailscale is confirmed by the
    /// 100.64.0.0/10 CGNAT range it always uses; the others are interface-name heuristics only
    /// (WireGuard <c>wg*</c>, OpenVPN <c>tun*</c>/<c>tap*</c>, ZeroTier <c>zt*</c>).
    /// </summary>
    private static string? OverlayLabel(SubnetAggregate agg)
    {
        foreach (SubnetInterface iface in agg.Interfaces)
        {
            string name = iface.InterfaceName?.ToLowerInvariant() ?? "";
            if (name.StartsWith("tailscale", StringComparison.Ordinal))
            {
                if (IPAddress.TryParse(iface.Ip, out IPAddress? ip) && TailscaleCgnat.Contains(ip))
                {
                    return "Tailscale";
                }

                continue;
            }

            if (name.StartsWith("wg", StringComparison.Ordinal))
            {
                return "WireGuard";
            }

            if (name.StartsWith("tun", StringComparison.Ordinal) || name.StartsWith("tap", StringComparison.Ordinal))
            {
                return "VPN";
            }

            if (name.StartsWith("zt", StringComparison.Ordinal))
            {
                return "ZeroTier";
            }
        }

        return null;
    }

    /// <summary>Collapses embedded newlines so a node label always renders on one line.</summary>
    private static string SanitizeLabel(string label) =>
        label.Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// True when a route's interface name is a Docker bridge — the default <c>docker0</c>, the swarm
    /// <c>docker_gwbridge</c>, or a user-defined <c>br-&lt;hex&gt;</c> bridge (Docker names these
    /// "br-" + the network id's hex prefix). Used as a Docker-API-free signal that a route's subnet
    /// is host-local NAT. The all-hex check on the suffix deliberately excludes router bridges like
    /// OpenWrt's <c>br-lan</c>/<c>br-wan</c>, which are routable L2 segments, not host-local NAT.
    /// </summary>
    private static bool IsDockerBridgeInterface(string? iface)
    {
        if (string.IsNullOrEmpty(iface))
        {
            return false;
        }

        if (iface is "docker0" or "docker_gwbridge")
        {
            return true;
        }

        if (!iface.StartsWith("br-", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = iface.AsSpan(3);
        if (suffix.Length < 8)
        {
            return false; // Docker uses 12 hex chars; require enough to avoid matching short names.
        }

        foreach (char c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<List<SubnetAggregate>> BuildAggregatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        Dictionary<string, SubnetAggregate> subnets = new(StringComparer.Ordinal);

        // Track 1 (docs/plans/l3-topology.md): a Docker bridge network is host-local NAT — its
        // CIDR (172.17.0.0/16 et al.) exists on every host running Docker but routes nowhere
        // between them, so identical CIDRs must NOT merge into one shared node. Keyed by
        // (device, canonical CIDR); macvlan/ipvlan/overlay are routable and stay globally keyed.
        Dictionary<(string Device, string Cidr), string?> hostLocalNets = new();
        await foreach ((string dnDevice, string dnCidr, string? dnName, string? dnDriver, string? _)
            in conn.ListDockerNetworksAsync(ct))
        {
            if (!string.Equals(dnDriver, "bridge", StringComparison.OrdinalIgnoreCase)
             || !IPNetwork.TryParse(dnCidr, out IPNetwork dnNet))
            {
                continue;
            }

            hostLocalNets[(dnDevice, dnNet.ToString())] = dnName;
        }

        // Route-table fallback for host-local detection. A Docker bridge is reached through a
        // docker0 / br-<hex> interface, which proj_device_routes records per (device, CIDR) — and
        // RouteCollector needs no Docker socket, so this classifies bridges even on hosts where the
        // authoritative proj_docker_networks projection is empty (the Docker API wasn't collectable,
        // or the projection hadn't been backfilled yet). Routes are materialized once here because
        // the pre-scan must fully populate hostLocalNets BEFORE the interface loop calls GetOrAdd —
        // otherwise a docker0 interface IP would create a global-keyed node first. Authoritative
        // Docker-API names win: TryAdd only fills (device, CIDR) pairs that path didn't cover.
        List<(string Device, string Destination, string? Iface)> routes = [];
        await foreach ((string device, string destination, string? iface) in conn.ListSubnetRoutesAsync(ct))
        {
            routes.Add((device, destination, iface));
            if (IsDockerBridgeInterface(iface) && IPNetwork.TryParse(destination, out IPNetwork brNet))
            {
                hostLocalNets.TryAdd((device, brNet.ToString()), iface);
            }
        }

        // Local: keys a host-local Docker-bridge subnet per (device, CIDR) so each host's copy
        // is its own node attached only to that host; every other subnet keeps global CIDR keying.
        SubnetAggregate GetOrAdd(IPNetwork network, string? device)
        {
            string cidr = network.ToString();
            bool hostLocal = device is not null && hostLocalNets.TryGetValue((device, cidr), out string? _);
            string key = hostLocal ? $"L {device} {cidr}" : cidr;
            if (!subnets.TryGetValue(key, out SubnetAggregate? agg))
            {
                agg = new SubnetAggregate(network);
                if (hostLocal && device is not null)
                {
                    agg.HostLocalDevice = device;
                    agg.DockerNetName = hostLocalNets[(device, cidr)];
                }

                subnets[key] = agg;
            }

            return agg;
        }

        await foreach ((string device, string? hostname, string? name, string? ipv4, int? ipv4PrefixLength)
            in conn.ListSubnetInterfacesAsync(ct))
        {
            if (ipv4 is null)
            {
                continue;
            }

            // Interfaces that already emit a full CIDR (e.g. the local NetworkCollector) parse
            // directly. Interfaces that must emit a bare IP (Google Wifi/OnHub — the bare-IP
            // meaning is an exact-match join key elsewhere) fall back to a CIDR synthesized from
            // the separately-captured prefix length, so an isolated subnet with no other peer
            // (e.g. the guest network) still gets a subnet on this page.
            string cidr = !ipv4.Contains('/') && ipv4PrefixLength is { } prefixLength
                ? $"{ipv4}/{prefixLength}"
                : ipv4;

            if (!TryParseCidr(cidr, out IPNetwork network, out IPAddress? hostAddress))
            {
                continue;
            }

            SubnetAggregate agg = GetOrAdd(network, device);
            agg.Sources.Add("I");
            agg.Interfaces.Add(new SubnetInterface(device, hostname, name, hostAddress!.ToString()));
        }

        await foreach ((string service, string scope, bool? enabled, string? startAddress, string? endAddress,
            string? subnetMask, string? gateway) in conn.ListTerrainDhcpScopesAsync(ct))
        {
            if (enabled == false
             || startAddress is not { Length: > 0 }
             || subnetMask is not { Length: > 0 }
             || !TryMaskedNetwork(startAddress, subnetMask, out IPNetwork network))
            {
                continue;
            }

            // DHCP scopes are service-reported LAN subnets, never a host's Docker bridge — global key.
            SubnetAggregate agg = GetOrAdd(network, null);
            agg.Sources.Add("D");
            agg.Name ??= scope;
            agg.Gateway ??= string.IsNullOrWhiteSpace(gateway) ? null : gateway;
            agg.DhcpService ??= service;
            agg.DhcpStart ??= startAddress;
            agg.DhcpEnd ??= endAddress;
        }

        foreach ((string device, string destination, string? _) in routes)
        {
            if (!IPNetwork.TryParse(destination, out IPNetwork network))
            {
                continue;
            }

            // Pass the device so a host's own docker0 connected route lands on that host's
            // per-host node instead of re-merging into a global CIDR node.
            GetOrAdd(network, device).Sources.Add("R");
        }

        // Default-route gateways only ever fill a gap on an already-known subnet — 0.0.0.0/0
        // isn't itself a subnet, so this never creates a new one.
        if (subnets.Count > 0)
        {
            await foreach ((string _, string? _, string? gateway) in conn.ListDefaultRoutesAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(gateway) || !IPAddress.TryParse(gateway, out IPAddress? gwAddr))
                {
                    continue;
                }

                SubnetAggregate? owner = subnets.Values.FirstOrDefault(a => a.Network.Contains(gwAddr));
                if (owner is null || owner.Gateway is not null)
                {
                    continue;
                }

                owner.Gateway = gateway;
                owner.Sources.Add("R");
            }
        }

        if (subnets.Count == 0)
        {
            return [];
        }

        List<SubnetAggregate> all = subnets.Values.ToList();
        await foreach (SubnetHostIp row in conn.ListSubnetHostIpsAsync(ct))
        {
            if (row.Ip is null || !IPAddress.TryParse(row.Ip, out IPAddress? addr))
            {
                continue;
            }

            string ip = row.Ip;

            foreach (SubnetAggregate agg in all)
            {
                if (agg.Network.Contains(addr))
                {
                    agg.HostIps.Add(ip);
                }
            }
        }

        return all;
    }

    /// <summary>
    /// Resolves a gateway IP to a device. Checks known interface IPs first — a device that
    /// self-reports this exact address is definitively the router, no ARP/fingerprint chain
    /// needed — and falls back to ARP-based fingerprint resolution otherwise. Skipping the
    /// interface check would mean a router's own subnet-attached interface and its resolved
    /// gateway identity get keyed differently (raw device string vs. fingerprinted device_id),
    /// splitting one physical router into two nodes on the L3 graph.
    /// </summary>
    private static async Task<(string? DeviceId, string? Hostname)> ResolveGatewayAsync(
        NpgsqlConnection conn,
        string? gatewayIp,
        Dictionary<string, SubnetInterface> interfaceByIp,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(gatewayIp))
        {
            return (null, null);
        }

        if (interfaceByIp.TryGetValue(gatewayIp, out SubnetInterface? owner))
        {
            return (owner.Device, owner.Hostname ?? owner.Device);
        }

        await foreach ((string? _, Guid? resolvedDeviceId, string? resolvedHostname, string? _) in
            conn.ResolveIpDeviceAsync(gatewayIp, ct))
        {
            return (resolvedDeviceId?.ToString(), resolvedHostname);
        }

        return (null, null);
    }

    /// <summary>Indexes every known interface by its IP address (first-wins) for gateway resolution.</summary>
    private static Dictionary<string, SubnetInterface> BuildInterfaceIpIndex(List<SubnetAggregate> subnets)
    {
        Dictionary<string, SubnetInterface> byIp = new(StringComparer.Ordinal);
        foreach (SubnetAggregate agg in subnets)
        {
            foreach (SubnetInterface iface in agg.Interfaces)
            {
                byIp.TryAdd(iface.Ip, iface);
            }
        }

        return byIp;
    }

    /// <summary>Parses an agent-emitted "ip/prefix" interface CIDR string.</summary>
    private static bool TryParseCidr(string ipv4, out IPNetwork network, out IPAddress? hostAddress)
    {
        network = default;
        hostAddress = null;

        int slash = ipv4.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(ipv4[..slash], out IPAddress? host))
        {
            return false;
        }

        hostAddress = host;
        return IPNetwork.TryParse(ipv4, out network);
    }

    /// <summary>Derives a canonical network from a DHCP scope's start address + dotted subnet mask.</summary>
    private static bool TryMaskedNetwork(string startAddress, string subnetMask, out IPNetwork network)
    {
        network = default;
        if (!IPAddress.TryParse(startAddress, out IPAddress? start)
         || !IPAddress.TryParse(subnetMask, out IPAddress? mask))
        {
            return false;
        }

        int prefixLength = 0;
        foreach (byte b in mask.GetAddressBytes())
        {
            prefixLength += System.Numerics.BitOperations.PopCount(b);
        }

        try
        {
            network = new IPNetwork(start, prefixLength);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed class IpAddressComparer : IComparer<IPAddress>
    {
        public static readonly IpAddressComparer Instance = new();

        public int Compare(IPAddress? x, IPAddress? y)
        {
            if (x is null || y is null)
            {
                return Comparer<IPAddress?>.Default.Compare(x, y);
            }

            byte[] xb = x.GetAddressBytes();
            byte[] yb = y.GetAddressBytes();
            if (xb.Length != yb.Length)
            {
                return xb.Length.CompareTo(yb.Length);
            }

            for (int i = 0; i < xb.Length; i++)
            {
                int cmp = xb[i].CompareTo(yb[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return 0;
        }
    }

    private sealed class SubnetAggregate
    {
        public SubnetAggregate(IPNetwork network) => Network = network;

        public IPNetwork Network { get; }
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> HostIps { get; } = new(StringComparer.Ordinal);
        public List<SubnetInterface> Interfaces { get; } = [];
        public string? Name { get; set; }
        public string? Gateway { get; set; }
        public string? DhcpService { get; set; }
        public string? DhcpStart { get; set; }
        public string? DhcpEnd { get; set; }

        /// <summary>
        /// Non-null when this subnet is a host-local Docker bridge, scoped to the owning device
        /// — the graph labels it with that host and it never merges with another host's identical
        /// CIDR. <see cref="DockerNetName" /> is the Docker network name (e.g. "bridge") for the label.
        /// </summary>
        public string? HostLocalDevice { get; set; }
        public string? DockerNetName { get; set; }

        /// <summary>
        /// Owning host label for a host-local subnet (its hostname, or the raw device id when no
        /// hostname is known) — the disambiguator when two hosts share an identical bridge CIDR.
        /// Null for ordinary (shared) subnets.
        /// </summary>
        public string? OwnerHost =>
            HostLocalDevice is null ? null : Interfaces.FirstOrDefault()?.Hostname ?? HostLocalDevice;
    }
}

public sealed record SubnetInterface(string Device, string? Hostname, string? InterfaceName, string Ip);

public sealed record SubnetListItem(
    string Cidr,
    string? Name,
    string? Gateway,
    string? GatewayDeviceId,
    string? GatewayHostname,
    int HostCount,
    IReadOnlyList<string> Sources,
    // Owning host for a host-local (Docker bridge) subnet — disambiguates two hosts' identical
    // bridge CIDR; null for an ordinary shared subnet. HostLocal marks the row non-routable
    // (keyed per host, see docs/plans/l3-topology.md Track 1).
    string? Host = null,
    bool HostLocal = false
);

public sealed record SubnetDetail(
    string Cidr,
    string? Name,
    string? Gateway,
    string? GatewayDeviceId,
    string? GatewayHostname,
    string? DhcpService,
    string? DhcpStart,
    string? DhcpEnd,
    int HostCount,
    IReadOnlyList<string> Sources,
    IReadOnlyList<SubnetInterface> Interfaces
);

/// <summary>Node kinds for <see cref="SubnetGraph" />: "subnet", "router", "internet", or "vpn".</summary>
public sealed record SubnetGraphNode(string Id, string Label, string Kind);

/// <summary><see cref="Via" /> is the interface name the edge traverses (e.g. "eth0", "docker0"), when known.</summary>
public sealed record SubnetGraphEdge(string FromId, string ToId, string? Via = null);

public sealed record SubnetGraph(IReadOnlyList<SubnetGraphNode> Nodes, IReadOnlyList<SubnetGraphEdge> Edges);