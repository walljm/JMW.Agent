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
                    agg.Name,
                    agg.Gateway,
                    gwDeviceId,
                    gwHostname,
                    agg.HostIps.Count,
                    agg.Sources.OrderBy(s => s, StringComparer.Ordinal).ToArray()
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

        void AddEdge(string fromId, string toId)
        {
            if (drawnEdges.Add((fromId, toId)))
            {
                edges.Add(new SubnetGraphEdge(fromId, toId));
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
            string label = agg.Network + (agg.Name is { Length: > 0 } n ? " · " + n : "")
                         + $" · {agg.HostIps.Count} hosts";
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
            AddEdge(routerId, subnetNodeId[agg]);
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
                AddEdge(routerId, subnetNodeId[agg]);
            }
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

    /// <summary>Collapses embedded newlines so a node label always renders on one line.</summary>
    private static string SanitizeLabel(string label) =>
        label.Replace('\n', ' ').Replace('\r', ' ');

    private static async Task<List<SubnetAggregate>> BuildAggregatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        Dictionary<string, SubnetAggregate> subnets = new(StringComparer.Ordinal);

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

            SubnetAggregate agg = GetOrAdd(subnets, network);
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

            SubnetAggregate agg = GetOrAdd(subnets, network);
            agg.Sources.Add("D");
            agg.Name ??= scope;
            agg.Gateway ??= string.IsNullOrWhiteSpace(gateway) ? null : gateway;
            agg.DhcpService ??= service;
            agg.DhcpStart ??= startAddress;
            agg.DhcpEnd ??= endAddress;
        }

        await foreach ((string _, string destination) in conn.ListSubnetRoutesAsync(ct))
        {
            if (!IPNetwork.TryParse(destination, out IPNetwork network))
            {
                continue;
            }

            GetOrAdd(subnets, network).Sources.Add("R");
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

    private static SubnetAggregate GetOrAdd(Dictionary<string, SubnetAggregate> subnets, IPNetwork network)
    {
        string key = network.ToString();
        if (!subnets.TryGetValue(key, out SubnetAggregate? agg))
        {
            agg = new SubnetAggregate(network);
            subnets[key] = agg;
        }

        return agg;
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
    IReadOnlyList<string> Sources
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

/// <summary>Node kinds for <see cref="SubnetGraph" />: "subnet", "router", or "internet".</summary>
public sealed record SubnetGraphNode(string Id, string Label, string Kind);

public sealed record SubnetGraphEdge(string FromId, string ToId);

public sealed record SubnetGraph(IReadOnlyList<SubnetGraphNode> Nodes, IReadOnlyList<SubnetGraphEdge> Edges);