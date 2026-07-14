using System.Globalization;

using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>A connected client station distilled from the diagnostic report.</summary>
public sealed record OnHubStation(
    string Ip,
    string? Mac, // obscured, e.g. "00e0bf1fc40*"; null if unknown
    string? Hostname, // clean mDNS name; masked/empty dropped
    string? Model, // device model (mDNS model=/md=, else UPnP modelName/modelNumber, else device_model)
    bool Connected,
    string? Oui = null, // raw OUI, 6 lowercase hex (server resolves vendor from real MAC)
    string? FriendlyName = null, // mDNS fn=, else UPnP friendlyName (e.g. "Kitchen Audio")
    string? Manufacturer = null, // UPnP manufacturer — the OnHub's own UPnP query result, no other OnHub source for this
    string? DeviceType = null, // mDNS-derived (e.g. "Nest-Audio", "Chromecast")
    string? CastId = null, // stable Google Cast device id (32 hex), decoupled from IP
    string? CastCapabilities = null, // raw mDNS ca= (opaque, undocumented bit layout)
    string? CastStatus = null, // raw mDNS st= (opaque, transient)
    string? CastRunningApp = null, // raw mDNS rs= (opaque, transient; empty when idle)
    IReadOnlyList<string>? Services = null, // mDNS service types, e.g. ["_googlecast._tcp"]
    string? ConnectionMedium = null, // "wired" | "wireless"
    string? Band = null, // "2.4GHz" | "5GHz"
    bool? Guest = null, // guest-network membership
    long? SignalDbm = null, // RSSI (negative dBm)
    double? TxRateMbps = null,
    double? RxRateMbps = null,
    long? RxBytes = null,
    long? TxBytes = null,
    long? ConnectedSeconds = null,
    bool IsMeshNode = false, // this station_id is itself a Google Wifi/OnHub mesh point (root/child of a mesh group), not a client device
    string? MeshApBssid = null, // real bssid of the physical mesh point currently relaying this client, resolved via mpp dump + mesh_node_info
    bool IsDhcpReserved = false, // the owner has an explicit DHCP reservation for this device in the router UI
    string? DhcpReservedIp = null, // the IP reserved in that dhcp_reservation entry (display-only; OnHub has no lease expiry data)
    DateTimeOffset? LastActiveAt = null, // hostapd's most recent "has been active" beacon-report line — temporal, not a snapshot
    string? LastActiveInterface = null, // band + main/guest at that last-active timestamp, e.g. "wlan-5000mhz"
    DateTimeOffset? LastRoamingAt = null, // most recent mesh handoff (hostapd IAPP ADD-notify)
    string? LastRoamingApIp = null // real LAN IP of the mesh AP that handled that handoff
)
{
    /// <summary>Service types, never null — empty when the client advertises none.</summary>
    public IReadOnlyList<string> ServiceTypes => Services ?? [];
}

/// <summary>
/// Distills client stations from a Google Wifi diagnostic report by joining four
/// per-client sources on IP (and, for telemetry, the obscured-MAC string):
/// • <c>ap-show --network_runtime_state</c> (field 9) — obscured MAC ↔ IP;
/// • <c>networkState.station_state_updates</c> (field 16) — connected flag, mDNS
/// name + services, OUI, wired/wireless, band, guest, and (for stations the OnHub itself
/// UPnP-queried) friendlyName/manufacturer/modelName/modelNumber via <c>upnp_attribute</c>
/// — the strongest identity signal for devices with no mDNS presence at all;
/// • <c>iw dev &lt;iface&gt; station dump</c> (field 9) — RSSI, tx/rx rate + bytes,
/// connected time (keyed by obscured MAC);
/// • <c>/proc/net/arp</c> (field 9) — additional (incl. wired) neighbors ↔ IP.
/// IP is the primary join key between the first two sources, since station_id is a
/// hash rather than a MAC. When that join misses (the two blobs disagree on IP for
/// the same station), <c>stationIdMappings</c> (field 12) gives a second path — it
/// maps each obscured MAC directly to the unmasked station_id used in field 16 — so
/// we can still recover the rich detail without relying on IP agreement.
/// networkState also carries <c>mesh_group.node_info</c> blocks listing the station_ids
/// that are themselves other Google Wifi/OnHub mesh points (satellites), not client
/// devices (networkConfig, field 5, carries the SAME membership under a differently-named
/// <c>group.root/child</c> block — verified identical against a live capture; only one
/// source is needed, and mesh_group is here in the source we already parse). Those
/// otherwise look like unlabeled connected clients (Google's own OUI, empty model/os
/// fields, masked mDNS name) and would be dropped as "nothing worth reporting" — we flag
/// them via <see cref="OnHubStation.IsMeshNode" /> instead so they're still emitted.
/// wanInfo (field 8 — NOT networkState, verified against a live capture) carries
/// <c>mesh_node_info { bssid, shmac }</c>, giving the REAL (unobscured) bssid of each mesh
/// point, keyed by its station_id (<c>shmac</c>). The mesh routing table (<c>iw ... mpp
/// dump</c>, field 9) maps each client's obscured MAC to the obscured MAC of the mesh
/// point currently relaying it — the exact same obscured format used everywhere else, so
/// no reconstruction is needed. Chaining client-obscured-MAC → mesh-point-obscured-MAC →
/// (via <c>stationIdMappings</c>, field 12) station_id → (via wanInfo's <c>mesh_node_info</c>)
/// real bssid tells us which physical AP each client currently associates through.
/// </summary>
public static class OnHubStations
{
    public static IReadOnlyList<OnHubStation> Extract(DiagnosticReport report)
    {
        Dictionary<string, string> macByIp = MacByIp(report);
        (Dictionary<string, NetInfo> netByIp, Dictionary<string, NetInfo> netByStationId) = NetInfoByIp(report.NetworkState);
        Dictionary<string, string> meshApBssidByStationId = MeshApBssidByStationId(report.WanInfo);
        Dictionary<string, string> stationIdByObscuredMac = StationIdByObscuredMac(report);
        Dictionary<string, string> meshApObscuredMacByClientMac = MeshApObscuredMacByClientMac(report);
        Dictionary<string, IwTelemetry> iwByMac = IwByMac(report);
        Dictionary<string, string> arpByIp = ArpByIp(report);
        IReadOnlyDictionary<string, OnHubHostapdActivity> hostapdByMac = OnHubHostapdLog.ExtractByObscuredMac(report);

        // Candidate IPs: runtime stations (ap-show) + reachable ARP neighbors + every
        // networkState station currently reporting connected.
        HashSet<string> ips = new(macByIp.Keys, StringComparer.Ordinal);
        foreach (string ip in arpByIp.Keys)
        {
            ips.Add(ip);
        }

        foreach ((string ip, NetInfo info) in netByIp)
        {
            if (info.Connected)
            {
                ips.Add(ip);
            }
        }

        List<OnHubStation> stations = [];
        foreach (string ip in ips)
        {
            string? mac = macByIp.TryGetValue(ip, out string? apMac)
                ? apMac
                : arpByIp.GetValueOrDefault(ip);

            netByIp.TryGetValue(ip, out NetInfo? net);
            if (net is null
                && mac is not null
                && stationIdByObscuredMac.TryGetValue(mac, out string? stationId)
                && netByStationId.TryGetValue(stationId, out NetInfo? netByMac))
            {
                net = netByMac;
            }

            // Exclude stations the networkState explicitly marks disconnected.
            if (net is { Connected: false })
            {
                continue;
            }

            IwTelemetry? tel = mac is not null ? iwByMac.GetValueOrDefault(mac) : null;

            string? hostname = Clean(net?.MdnsName);
            string? friendly = net?.FriendlyName;
            string? model = net?.Model;
            string? manufacturer = net?.Manufacturer;
            string? oui = net?.Oui ?? OuiOf(mac);
            string? band = net?.Band ?? tel?.Band;
            bool isMeshNode = net?.IsMeshNode ?? false;
            bool isDhcpReserved = net?.IsDhcpReserved ?? false;

            string? meshApBssid = mac is not null
                && meshApObscuredMacByClientMac.TryGetValue(mac, out string? meshApObscuredMac)
                && stationIdByObscuredMac.TryGetValue(meshApObscuredMac, out string? meshApStationId)
                && meshApBssidByStationId.TryGetValue(meshApStationId, out string? bssid)
                    ? bssid
                    : null;

            OnHubHostapdActivity? hostapd = mac is not null ? hostapdByMac.GetValueOrDefault(mac) : null;

            if (mac is null && hostname is null && friendly is null && model is null && manufacturer is null && net?.DeviceType is null && !isMeshNode && !isDhcpReserved)
            {
                // Nothing worth reporting for this IP.
                continue;
            }

            stations.Add(
                new OnHubStation(
                    ip,
                    mac,
                    hostname,
                    model,
                    Connected: true,
                    Oui: oui,
                    FriendlyName: friendly,
                    Manufacturer: manufacturer,
                    DeviceType: net?.DeviceType,
                    CastId: net?.CastId,
                    CastCapabilities: net?.CastCapabilities,
                    CastStatus: net?.CastStatus,
                    CastRunningApp: net?.CastRunningApp,
                    Services: net?.Services,
                    ConnectionMedium: net?.ConnectionMedium,
                    Band: band,
                    Guest: net?.Guest,
                    SignalDbm: tel?.SignalDbm,
                    TxRateMbps: tel?.TxRateMbps,
                    RxRateMbps: tel?.RxRateMbps,
                    RxBytes: tel?.RxBytes,
                    TxBytes: tel?.TxBytes,
                    ConnectedSeconds: tel?.ConnectedSeconds,
                    IsMeshNode: isMeshNode,
                    MeshApBssid: meshApBssid,
                    IsDhcpReserved: isDhcpReserved,
                    DhcpReservedIp: net?.DhcpReservedIp,
                    LastActiveAt: hostapd?.LastActiveAt,
                    LastActiveInterface: hostapd?.LastActiveInterface,
                    LastRoamingAt: hostapd?.LastRoamingAt,
                    LastRoamingApIp: hostapd?.LastRoamingApIp
                )
            );
        }

        stations.Sort((a, b) => string.CompareOrdinal(a.Ip, b.Ip));
        return stations;
    }

    // ── ap-show: obscured MAC ↔ IP ────────────────────────────────────────────

    private const string ApShowCommandMarker = "network_runtime_state";

    private static Dictionary<string, string> MacByIp(DiagnosticReport report)
    {
        Dictionary<string, string> macByIp = new(StringComparer.Ordinal);

        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (!cmd.Command.Contains(ApShowCommandMarker, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (TextNode station in FindAll(OnHubTextFormat.Parse(cmd.Output), "station_info"))
            {
                string? mac = NormalizeObscuredMac(station.ScalarOf("mac_address"));
                if (mac is null)
                {
                    continue;
                }

                foreach (TextNode ipNode in station.ChildrenNamed("ipv4_addresses"))
                {
                    if (ipNode.Value is { Length: > 0 } ip)
                    {
                        macByIp[ip] = mac;
                    }
                }
            }
        }

        return macByIp;
    }

    // ── networkState station_info: rich per-client detail ──────────────────────

    private sealed record NetInfo(
        string? StationId,
        bool IsMeshNode,
        bool IsDhcpReserved,
        string? DhcpReservedIp,
        bool Connected,
        string? MdnsName,
        string? Model,
        string? Oui,
        string? FriendlyName,
        string? Manufacturer,
        string? DeviceType,
        string? CastId,
        string? CastCapabilities,
        string? CastStatus,
        string? CastRunningApp,
        IReadOnlyList<string>? Services,
        string? ConnectionMedium,
        string? Band,
        bool? Guest
    );

    private static (Dictionary<string, NetInfo> ByIp, Dictionary<string, NetInfo> ByStationId) NetInfoByIp(string networkState)
    {
        Dictionary<string, NetInfo> byIp = new(StringComparer.Ordinal);
        Dictionary<string, NetInfo> byStationId = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(networkState))
        {
            return (byIp, byStationId);
        }

        IReadOnlyList<TextNode> roots = OnHubTextFormat.Parse(networkState);
        HashSet<string> meshNodeStationIds = MeshNodeStationIds(roots);
        Dictionary<string, string?> dhcpReservedIpByStationId = DhcpReservedIpByStationId(roots);

        foreach (TextNode station in FindAll(roots, "station_info"))
        {
            string? connected = station.ScalarOf("connected");
            OnHubDnsSdResult dns = OnHubDnsSd.Parse(station);
            OnHubUpnpResult upnp = OnHubUpnp.Parse(station);
            string? stationId = station.ScalarOf("station_id");
            bool isDhcpReserved = stationId is { Length: > 0 } && dhcpReservedIpByStationId.ContainsKey(stationId);
            string? dhcpReservedIp = isDhcpReserved ? dhcpReservedIpByStationId[stationId!] : null;

            string? wireless = station.ScalarOf("wireless");
            string? medium = wireless switch
            {
                "true" => "wireless",
                "false" => "wired",
                _ => null,
            };

            NetInfo info = new(
                StationId: stationId,
                IsMeshNode: stationId is { Length: > 0 } && meshNodeStationIds.Contains(stationId),
                IsDhcpReserved: isDhcpReserved,
                DhcpReservedIp: dhcpReservedIp,
                // Absent flag ⇒ treat as connected (mirrors the runtime list's implied state).
                Connected: !string.Equals(connected, "false", StringComparison.OrdinalIgnoreCase),
                MdnsName: station.ScalarOf("mdns_name"),
                Model: dns.Model ?? upnp.Model ?? Clean(station.ScalarOf("device_model")),
                Oui: NormalizeOui(station.ScalarOf("oui")),
                FriendlyName: dns.Friendly ?? upnp.FriendlyName,
                Manufacturer: upnp.Manufacturer,
                DeviceType: dns.DeviceType,
                CastId: dns.CastId,
                CastCapabilities: dns.CastCapabilities,
                CastStatus: dns.CastStatus,
                CastRunningApp: dns.CastRunningApp,
                Services: dns.Services.Count > 0 ? dns.Services : null,
                ConnectionMedium: medium,
                Band: BandOf(station.ScalarOf("wireless_interface")),
                Guest: station.ScalarOf("guest") switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null,
                }
            );

            foreach (TextNode ipNode in station.ChildrenNamed("ip_addresses"))
            {
                if (ipNode.Value is { Length: > 0 } ip)
                {
                    byIp[ip] = info;
                }
            }

            if (stationId is { Length: > 0 })
            {
                byStationId[stationId] = info;
            }
        }

        return (byIp, byStationId);
    }

    // ── wanInfo (field 8): mesh point station_id ↔ real (unobscured) bssid ─────

    /// <summary>
    /// Collects <c>mesh_node_info.{bssid, shmac}</c> from <c>wanInfo</c> (field 8) —
    /// NOT networkState (field 16), verified against a live capture. <c>shmac</c> is a
    /// station_id; <c>bssid</c> is the only genuinely unobscured MAC anywhere in the
    /// report (everything else — client MACs, even router-interface MACs — is obscured).
    /// </summary>
    private static Dictionary<string, string> MeshApBssidByStationId(string wanInfo)
    {
        Dictionary<string, string> byStationId = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(wanInfo))
        {
            return byStationId;
        }

        foreach (TextNode node in FindAll(OnHubTextFormat.Parse(wanInfo), "mesh_node_info"))
        {
            string? shmac = node.ScalarOf("shmac");
            string? bssid = node.ScalarOf("bssid");
            if (shmac is { Length: > 0 } && bssid is { Length: > 0 })
            {
                byStationId[shmac] = bssid;
            }
        }

        return byStationId;
    }

    // ── mesh_group: station_ids that are themselves mesh points, not clients ───

    /// <summary>
    /// Collects station_ids listed as <c>mesh_group.node_info.id</c> — the satellite
    /// access points forming the Wifi mesh. Without this, a mesh point shows up in
    /// station_state_updates as an unlabeled connected client (Google's own OUI,
    /// masked mDNS name, empty model/os fields) and gets dropped as uninteresting.
    /// networkConfig (field 5) carries the SAME membership under a differently-named,
    /// differently-shaped block (<c>group.id/root/child.station_id</c>) — verified
    /// identical group id and station_ids/IPs against a live capture; we only need one
    /// source, and this one (field 16, already parsed here) avoids adding a new input.
    /// </summary>
    private static HashSet<string> MeshNodeStationIds(IReadOnlyList<TextNode> roots)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (TextNode meshGroup in FindAll(roots, "mesh_group"))
        {
            foreach (TextNode nodeInfo in meshGroup.ChildrenNamed("node_info"))
            {
                if (nodeInfo.ScalarOf("id") is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    // ── dhcp_reservations: station_ids the owner explicitly reserved an IP for ──

    /// <summary>
    /// Collects <c>dhcp_reservations.dhcp_reservation.{id, ip_address}</c> — an explicit DHCP
    /// reservation configured by the owner in the router UI, a much stronger "known, owned
    /// device" signal than a bare sighting. The reserved IP is display-only (the OnHub cannot
    /// report true DHCP lease data — no expiry/renewal exists anywhere in the diagnostic
    /// report), so it's carried alongside the flag purely for the device-detail view.
    /// networkConfig (field 5) carries the same 21 IPs under a duplicate singular
    /// <c>dhcp_reservation</c> list, but keyed by a fully-masked MAC — useless on its
    /// own, so we only read the station_id-keyed form here (field 16, already parsed).
    /// Every reservation with an id gets an entry (value null when ip_address is absent) so
    /// the IsDhcpReserved flag's meaning is unchanged from before this method also captured IPs.
    /// </summary>
    private static Dictionary<string, string?> DhcpReservedIpByStationId(IReadOnlyList<TextNode> roots)
    {
        Dictionary<string, string?> ipByStationId = new(StringComparer.Ordinal);
        foreach (TextNode reservations in FindAll(roots, "dhcp_reservations"))
        {
            foreach (TextNode reservation in reservations.ChildrenNamed("dhcp_reservation"))
            {
                if (reservation.ScalarOf("id") is { Length: > 0 } id)
                {
                    ipByStationId[id] = reservation.ScalarOf("ip_address") is { Length: > 0 } ip ? ip : null;
                }
            }
        }

        return ipByStationId;
    }

    // ── stationIdMappings: obscured MAC ↔ unmasked station_id ──────────────────

    private static Dictionary<string, string> StationIdByObscuredMac(DiagnosticReport report)
    {
        Dictionary<string, string> byMac = new(StringComparer.Ordinal);

        foreach (StationIdMapping mapping in report.StationIdMappings)
        {
            if (NormalizeObscuredMac(mapping.ObscuredMac) is { } mac && mapping.StationId is { Length: > 0 })
            {
                byMac[mac] = mapping.StationId;
            }
        }

        return byMac;
    }

    // ── mesh routing table: client obscured MAC ↔ serving mesh point's obscured MAC ──

    private static Dictionary<string, string> MeshApObscuredMacByClientMac(DiagnosticReport report)
    {
        Dictionary<string, string> byClientMac = new(StringComparer.Ordinal);

        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (!cmd.Command.Contains("mpp dump", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string raw in cmd.Output.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith("DEST ADDR", StringComparison.Ordinal))
                {
                    continue; // header
                }

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                if (NormalizeObscuredMac(parts[0]) is { } client && NormalizeObscuredMac(parts[1]) is { } proxy)
                {
                    byClientMac[client] = proxy;
                }
            }
        }

        return byClientMac;
    }

    // ── iw station dump: per-client wireless telemetry ─────────────────────────

    private sealed record IwTelemetry(
        long? SignalDbm,
        double? TxRateMbps,
        double? RxRateMbps,
        long? RxBytes,
        long? TxBytes,
        long? ConnectedSeconds,
        string? Band
    );

    private static Dictionary<string, IwTelemetry> IwByMac(DiagnosticReport report)
    {
        Dictionary<string, IwTelemetry> byMac = new(StringComparer.Ordinal);
        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (cmd.Command.Contains("station dump", StringComparison.Ordinal))
            {
                ParseIwStationDump(cmd.Output, byMac);
            }
        }

        return byMac;
    }

    private static void ParseIwStationDump(string output, Dictionary<string, IwTelemetry> byMac)
    {
        string? mac = null;
        string? band = null;
        long? signal = null;
        double? txRate = null;
        double? rxRate = null;
        long? rxBytes = null;
        long? txBytes = null;
        long? connected = null;
        string? pendingLabel = null;

        void Flush()
        {
            if (mac is not null)
            {
                byMac[mac] = new IwTelemetry(signal, txRate, rxRate, rxBytes, txBytes, connected, band);
            }
        }

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("Station ", StringComparison.Ordinal))
            {
                Flush();
                mac = null;
                band = null;
                signal = null;
                txRate = null;
                rxRate = null;
                rxBytes = null;
                txBytes = null;
                connected = null;
                pendingLabel = null;

                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    mac = NormalizeObscuredMac(parts[1]);
                }

                int on = trimmed.IndexOf("(on ", StringComparison.Ordinal);
                if (on >= 0)
                {
                    string iface = trimmed[(on + 4)..].TrimEnd(')').Trim();
                    band = BandOf(iface);
                }

                continue;
            }

            if (mac is null)
            {
                continue;
            }

            string label;
            string value;
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                label = line[..colon].Trim();
                value = line[(colon + 1)..].Trim();
            }
            else if (pendingLabel is not null)
            {
                label = pendingLabel;
                value = trimmed;
            }
            else
            {
                continue;
            }

            // Some captures split "label:" and its value across lines.
            if (value.Length == 0)
            {
                pendingLabel = label;
                continue;
            }

            pendingLabel = null;

            switch (label)
            {
                case "signal":
                    signal = OnHubTextFormat.ParseLong(FirstToken(value));
                    break;
                case "tx bitrate":
                    txRate = ParseDouble(FirstToken(value));
                    break;
                case "rx bitrate":
                    rxRate = ParseDouble(FirstToken(value));
                    break;
                case "rx bytes":
                    rxBytes = OnHubTextFormat.ParseLong(FirstToken(value));
                    break;
                case "tx bytes":
                    txBytes = OnHubTextFormat.ParseLong(FirstToken(value));
                    break;
                case "connected time":
                    connected = OnHubTextFormat.ParseLong(FirstToken(value));
                    break;
            }
        }

        Flush();
    }

    // ── /proc/net/arp: reachable neighbours (incl. wired) ──────────────────────

    private static Dictionary<string, string> ArpByIp(DiagnosticReport report)
    {
        Dictionary<string, string> byIp = new(StringComparer.Ordinal);

        // Real captures carry /proc/net/arp as a raw file (field 2), not a CommandOutput
        // (field 9) — a live capture had exactly one arp table, and it was here. The
        // CommandOutput path is kept too in case some firmware/report variant uses it.
        foreach (Proto.File file in report.Files)
        {
            if (string.Equals(file.Path, "/proc/net/arp", StringComparison.Ordinal))
            {
                ParseArpTable(file.Content.ToStringUtf8(), byIp);
            }
        }

        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (string.Equals(cmd.Command, "/proc/net/arp", StringComparison.Ordinal))
            {
                ParseArpTable(cmd.Output, byIp);
            }
        }

        return byIp;
    }

    private static void ParseArpTable(string output, Dictionary<string, string> byIp)
    {
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Contains("IP address", StringComparison.Ordinal))
            {
                continue; // header
            }

            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || !string.Equals(f[2], "0x2", StringComparison.Ordinal))
            {
                continue; // not a complete/reachable entry
            }

            if (NormalizeObscuredMac(f[3]) is { } mac)
            {
                byIp[f[0]] = mac;
            }
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Canonicalizes an obscured MAC to 12 lowercase chars: 11 hex nibbles plus a
    /// trailing '*'. Returns null if the value is absent, fully masked, or malformed.
    /// </summary>
    public static string? NormalizeObscuredMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string mac = raw.Trim().ToLowerInvariant();
        if (mac.Length != 12 || mac[11] != '*')
        {
            return null;
        }

        for (int i = 0; i < 11; i++)
        {
            if (!Uri.IsHexDigit(mac[i]))
            {
                return null; // e.g. fully-masked "************"
            }
        }

        return mac;
    }

    /// <summary>Normalizes a reported OUI to 6 lowercase hex, or null if malformed.</summary>
    private static string? NormalizeOui(string? oui)
    {
        if (string.IsNullOrWhiteSpace(oui))
        {
            return null;
        }

        string hex = new(oui.Where(Uri.IsHexDigit).Take(6).Select(char.ToLowerInvariant).ToArray());
        return hex.Length == 6 ? hex : null;
    }

    /// <summary>Extracts the OUI (first 6 hex) from an obscured MAC, or null.</summary>
    private static string? OuiOf(string? mac) => NormalizeOui(mac);

    /// <summary>Maps a Google Wifi interface name to a Wi-Fi band, or null.</summary>
    private static string? BandOf(string? iface)
    {
        if (string.IsNullOrEmpty(iface))
        {
            return null;
        }

        if (iface.Contains("2400", StringComparison.Ordinal))
        {
            return "2.4GHz";
        }

        return iface.Contains("5000", StringComparison.Ordinal) ? "5GHz" : null;
    }

    /// <summary>Non-empty, non-masked scalar, or null.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }

    private static string FirstToken(string value)
    {
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? value : value[..space];
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Depth-first search for every node with the given name.</summary>
    private static IEnumerable<TextNode> FindAll(IReadOnlyList<TextNode> roots, string name)
    {
        Stack<TextNode> stack = new();
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            stack.Push(roots[i]);
        }

        while (stack.Count > 0)
        {
            TextNode node = stack.Pop();
            if (string.Equals(node.Name, name, StringComparison.Ordinal))
            {
                yield return node;
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }
}