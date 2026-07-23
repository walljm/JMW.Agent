using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Builds the L2 (physical/port-adjacency) topology graph for the Subnets page — distinct from
/// <see cref="SubnetsApi.GetGraphAsync" />'s L3 subnet/router graph. Nodes are devices (switch,
/// router, host, AP — not subnets); edges are LLDP-observed port adjacencies, keyed
/// {fromDevice, fromPort, toDevice, toPort, via}. Source: <c>Device[].Neighbor[]</c> facts (see
/// docs/plans/d3-l2-l3.md), which have no dedicated projection table — read directly from
/// facts_history via <see cref="ReportingQueries.ListNeighborFactsAsync" />.
/// </summary>
public static class L2TopologyApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/l2-topology", GetGraph)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static async Task<IResult> GetGraph(NpgsqlDataSource db, CancellationToken ct)
    {
        L2Graph graph = await GetGraphAsync(db, ct);
        return Results.Ok(graph);
    }

    public static async Task<L2Graph> GetGraphAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<L2GraphNode> nodes = [];
        List<L2GraphEdge> edges = [];
        Dictionary<string, string> deviceLabelById = new(StringComparer.Ordinal);
        Dictionary<string, string> unknownNodeIdByIdentity = new(StringComparer.Ordinal);
        HashSet<string> drawnPairs = new(StringComparer.Ordinal);
        int unknownSeq = 0;

        void EnsureDeviceNode(string deviceId, string? label)
        {
            if (deviceLabelById.ContainsKey(deviceId))
            {
                // Prefer a real hostname over a bare device id if one shows up later.
                if (label is { Length: > 0 } && IsDeviceIdLabel(deviceLabelById[deviceId], deviceId))
                {
                    deviceLabelById[deviceId] = label;
                    L2GraphNode existing = nodes.First(n => n.Id == deviceId);
                    nodes[nodes.IndexOf(existing)] = existing with { Label = label };
                }

                return;
            }

            string resolvedLabel = label is { Length: > 0 } ? label : deviceId;
            deviceLabelById[deviceId] = resolvedLabel;
            nodes.Add(new L2GraphNode(deviceId, resolvedLabel, "device"));
        }

        string EnsureUnknownNode(string identityKey, string label, string kind = "unknown")
        {
            if (unknownNodeIdByIdentity.TryGetValue(identityKey, out string? existingId))
            {
                return existingId;
            }

            string id = "unk" + unknownSeq++;
            unknownNodeIdByIdentity[identityKey] = id;
            nodes.Add(new L2GraphNode(id, label, kind));
            return id;
        }

        List<(string Device, string? Hostname, string? LocalPort, string? RemoteChassisId, string? RemotePortId,
            string? RemoteSysName, string? RemoteMac, string? RemoteIp, string? Protocol)> rows = [];
        await foreach ((string? device, string? _, string? hostname, string? localPort, string? remoteChassisId,
            string? remotePortId, string? remoteSysName, string? remoteMac, string? remoteIp, string? protocol)
            in conn.ListNeighborFactsAsync(ct))
        {
            // key_values ->> 'Device' is NULL-typed to Postgres (it's a jsonb text-extraction
            // operator), but every fact row always has a Device key in practice — skip the
            // (never-expected) case defensively rather than propagate a null device id.
            if (device is not { Length: > 0 })
            {
                continue;
            }

            rows.Add((device, hostname, localPort, remoteChassisId, remotePortId, remoteSysName, remoteMac, remoteIp,
                protocol));
        }

        foreach ((string device, string? hostname, string? localPort, string? remoteChassisId,
            string? remotePortId, string? remoteSysName, string? remoteMac, string? remoteIp, string? protocol) in
            rows)
        {
            EnsureDeviceNode(device, hostname);

            string? remoteDeviceId = null;
            string? remoteHostname = null;

            if (remoteIp is { Length: > 0 })
            {
                await foreach ((string? _, Guid? resolvedId, string? resolvedHostname, string? _) in
                    conn.ResolveIpDeviceAsync(remoteIp, ct))
                {
                    remoteDeviceId = resolvedId?.ToString();
                    remoteHostname = resolvedHostname;
                    break;
                }
            }

            if (remoteDeviceId is null && remoteMac is { Length: > 0 })
            {
                await foreach ((Guid resolvedId, string? resolvedHostname) in conn.ResolveMacDeviceAsync(
                                   remoteMac,
                                   ct
                               ))
                {
                    remoteDeviceId = resolvedId.ToString();
                    remoteHostname = resolvedHostname;
                    break;
                }
            }

            string toId;
            if (remoteDeviceId is { Length: > 0 })
            {
                EnsureDeviceNode(remoteDeviceId, remoteHostname);
                toId = remoteDeviceId;
            }
            else
            {
                string identityKey = remoteChassisId ?? remoteMac ?? remoteSysName ?? remoteIp ?? "unknown";
                string label = remoteSysName ?? remoteMac ?? remoteIp ?? "Unknown neighbor";
                toId = EnsureUnknownNode(identityKey, label);
            }

            if (string.Equals(device, toId, StringComparison.Ordinal))
            {
                continue; // guard against a self-referential row; never expected in practice
            }

            // LLDP typically reports each physical link from both ends — draw it once.
            string pairKey = string.CompareOrdinal(device, toId) <= 0 ? $"{device}|{toId}" : $"{toId}|{device}";
            if (drawnPairs.Add(pairKey))
            {
                edges.Add(new L2GraphEdge(device, localPort, toId, remotePortId, protocol ?? "lldp"));
            }
        }

        // Google Wifi/OnHub mesh points: we have no direct fingerprint for a satellite mesh
        // point, only the real BSSID of whichever one is currently relaying a given client (see
        // OnHubStations.cs). Represent that BSSID as its own node — a real device node if it
        // happens to be independently known (e.g. also ARP-discovered), else a synthetic "mesh"
        // node — so the satellite still shows up on the map instead of silently vanishing into
        // "client talks to the primary OnHub" with no relay in between.
        Dictionary<string, string> meshNodeIdByBssid = new(StringComparer.OrdinalIgnoreCase);
        List<(string? Mac, string? ObscuredMac, string? Hostname, string? MeshApBssid)> meshRows = [];
        await foreach ((string? mm_mac, string? mm_obscuredMac, string? mm_hostname, string? mm_meshApBssid) in
            conn.ListMeshRelayedClientsAsync(ct))
        {
            meshRows.Add((mm_mac, mm_obscuredMac, mm_hostname, mm_meshApBssid));
        }

        foreach ((string? mac, string? obscuredMac, string? hostname, string? meshApBssid) in meshRows)
        {
            // The query already filters mesh_ap_bssid IS NOT NULL; this is just the SQL->CLR
            // nullability gap (a UNION/JOIN result reports nullable even for a NOT NULL column).
            if (meshApBssid is null)
            {
                continue;
            }

            string? clientDeviceId = null;
            string? clientHostname = null;
            if (mac is { Length: > 0 })
            {
                await foreach ((Guid resolvedId, string? resolvedHostname) in conn.ResolveMacDeviceAsync(mac, ct))
                {
                    clientDeviceId = resolvedId.ToString();
                    clientHostname = resolvedHostname;
                    break;
                }
            }

            string clientId;
            if (clientDeviceId is { Length: > 0 })
            {
                EnsureDeviceNode(clientDeviceId, clientHostname ?? hostname);
                clientId = clientDeviceId;
            }
            else
            {
                string identityKey = mac ?? obscuredMac ?? hostname ?? meshApBssid;
                clientId = EnsureUnknownNode(identityKey, hostname ?? mac ?? obscuredMac ?? "Unknown client");
            }

            if (!meshNodeIdByBssid.TryGetValue(meshApBssid, out string? meshId))
            {
                string? meshDeviceId = null;
                string? meshHostname = null;
                await foreach ((Guid resolvedId, string? resolvedHostname) in
                    conn.ResolveMacDeviceAsync(meshApBssid, ct))
                {
                    meshDeviceId = resolvedId.ToString();
                    meshHostname = resolvedHostname;
                    break;
                }

                if (meshDeviceId is { Length: > 0 })
                {
                    EnsureDeviceNode(meshDeviceId, meshHostname);
                    meshId = meshDeviceId;
                }
                else
                {
                    meshId = EnsureUnknownNode(meshApBssid, $"Mesh point ({meshApBssid})", "mesh");
                }

                meshNodeIdByBssid[meshApBssid] = meshId;
            }

            if (string.Equals(clientId, meshId, StringComparison.Ordinal))
            {
                continue; // the mesh point relaying itself — never expected in practice
            }

            string meshPairKey = string.CompareOrdinal(clientId, meshId) <= 0
                ? $"{clientId}|{meshId}"
                : $"{meshId}|{clientId}";
            if (drawnPairs.Add(meshPairKey))
            {
                edges.Add(new L2GraphEdge(clientId, null, meshId, null, "mesh"));
            }
        }

        return new L2Graph(nodes, edges);
    }

    // True when `label` looks like it's just the bare device id (no hostname resolved yet).
    private static bool IsDeviceIdLabel(string label, string deviceId) =>
        string.Equals(label, deviceId, StringComparison.Ordinal);
}

/// <summary>
/// Node kinds for <see cref="L2Graph" />: "device" (a known Device[] row), "unknown" (an LLDP
/// neighbor that couldn't be resolved to a known device by IP or MAC), or "mesh" (a Google
/// Wifi/OnHub mesh point known only by BSSID — see GetGraphAsync's mesh-relay section).
/// </summary>
public sealed record L2GraphNode(string Id, string Label, string Kind);

/// <summary>
/// One physical port adjacency. FromPort/ToPort are the local interface labels each side
/// reported for itself — they are independently sourced, not a shared numbering scheme, so they
/// should never be compared to each other.
/// </summary>
public sealed record L2GraphEdge(string FromId, string? FromPort, string ToId, string? ToPort, string Via);

public sealed record L2Graph(IReadOnlyList<L2GraphNode> Nodes, IReadOnlyList<L2GraphEdge> Edges);