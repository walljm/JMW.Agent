using System.IO.Compression;

using Google.Protobuf;

using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Device collector for Google Wifi / Nest Wifi (OnHub) access points, reached
/// through the local, unauthenticated <c>http://&lt;ap-ip&gt;/api/v1/</c> API on the
/// AP itself — NOT the retired Google cloud API.
/// It identifies the AP as a router device (fingerprinted by the report's stable
/// 128-bit hardware id) and emits each connected client station as a
/// Device[].Discovered[] neighbor fact keyed by IP — the same shape the SNMP/ARP
/// collectors produce — so stations flow through the existing projection +
/// DiscoveryMaterializer pipeline.
/// MAC handling: the firmware obscures every MAC (last hex nibble → '*'). The
/// obscured value is emitted verbatim; the server reconstructs the full MAC from
/// its known-MAC set before it is ever used as a device fingerprint.
/// Target: Target.CollectorType == "google-wifi", Target.Endpoint = the AP IP
/// (e.g. 192.168.1.1). No credentials.
/// </summary>
public sealed class GoogleWifiCollector : IDeviceCollector
{
    // The diagnostic report is large and slow to produce (tens of seconds), so the
    // client is given a generous timeout well above the observed ~40s.
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(4),
    };

    private const string ProtocolName = "google-wifi";
    private const string SourceName = "google-wifi";

    private readonly OnHubClient _client;
    private readonly ILogger<GoogleWifiCollector> _logger = AgentLog.CreateLogger<GoogleWifiCollector>();

    public GoogleWifiCollector() : this(new OnHubClient(SharedHttp)) { }

    /// <summary>Test seam: inject a client backed by a mock HttpMessageHandler.</summary>
    public GoogleWifiCollector(OnHubClient client)
    {
        _client = client;
    }

    public string CollectorType => ProtocolName;

    public bool CanCollect(Target target) =>
        target.CollectorType is { } p && p.Equals(ProtocolName, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(target.Endpoint))
        {
            GoogleWifiCollectorLog.MissingAddress(_logger);
            return [];
        }

        string host = target.Endpoint.Trim();

        DiagnosticReport report;
        try
        {
            report = await _client.GetDiagnosticReportAsync(host, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GoogleWifiCollectorLog.ReportFailed(_logger, ex, host);
            return [];
        }

        // Identity: the report's clean 128-bit hardware id (report field 21). Unlike
        // the platform "hardwareId" string, this is per-unit, so distinct APs stay
        // distinct devices.
        if (string.IsNullOrWhiteSpace(report.DeviceId))
        {
            GoogleWifiCollectorLog.NoDeviceId(_logger, host);
            return [];
        }

        // Router facts are best-effort enrichment; a status failure must not sink
        // the (more valuable) station discovery.
        OnHubStatus? status = null;
        try
        {
            status = await _client.GetStatusAsync(host, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GoogleWifiCollectorLog.StatusFailed(_logger, ex, host);
        }

        DeviceIdentity identity = new(
            Fingerprints: [new Fingerprint(FingerprintType.GoogleWifiDeviceId, report.DeviceId)],
            Kind: "router",
            Vendor: "google",
            OsFamily: null,
            OsVersion: status?.Software?.SoftwareVersion
        );
        string deviceId = await context.RegisterProbeAsync(identity, ct);

        List<Fact> facts =
        [
            Fact.Create(FactPaths.DeviceVendor, [deviceId], "Google"),
            Fact.Create(FactPaths.DeviceKind, [deviceId], "router"),
            Fact.Create(FactPaths.HwSystemVendor, [deviceId], "Google"),
            Fact.Create(FactPaths.HwSystemSerial, [deviceId], report.DeviceId),
        ];

        if (status is not null)
        {
            AddRouterFacts(facts, deviceId, status);
        }

        AddApReportFacts(facts, deviceId, report);
        AddInterfaceFacts(facts, deviceId, report, host, status);
        AddStorageFacts(facts, deviceId, report);
        AddWanFacts(facts, deviceId, report);
        AddBridgeVlanFacts(facts, deviceId, report);

        HashSet<string> seenIps = new(StringComparer.Ordinal);
        foreach (OnHubStation station in OnHubStations.Extract(report))
        {
            seenIps.Add(station.Ip);
            AddStationFacts(facts, deviceId, station);
        }

        // nestedReport (field 15) is a complete, independently-parseable diagnostic report
        // from a different physical mesh unit (a satellite Wifi Point) — verified via a live
        // capture (different deviceId, ~3s apart). We don't model the satellite as its own
        // Device[] (would need a second RegisterProbeAsync-style identity, which
        // ICollectionContext doesn't support), so its stations are merged into this same
        // Discovered[] list — additively, only for IPs the outer report didn't already cover,
        // so the primary's own (already-tested) view always wins on overlap.
        if (TryParseNestedReport(report.NestedReport, _logger, out DiagnosticReport? nested))
        {
            foreach (OnHubStation station in OnHubStations.Extract(nested))
            {
                if (seenIps.Add(station.Ip))
                {
                    AddStationFacts(facts, deviceId, station);
                }
            }
        }

        return facts;
    }

    /// <summary>
    /// Decompresses and parses <c>nestedReport</c> as another <see cref="DiagnosticReport" /> —
    /// same message type as the outer report. Tolerant of empty/malformed/truncated input
    /// (some captures truncate embedded blobs to a size budget): a parse failure here
    /// degrades to "no satellite stations", never fails the whole collection cycle.
    /// </summary>
    private static bool TryParseNestedReport(
        ByteString bytes,
        ILogger logger,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DiagnosticReport? nested
    )
    {
        nested = null;
        if (bytes.IsEmpty)
        {
            return false;
        }

        try
        {
            using MemoryStream compressed = new(bytes.ToByteArray());
            using GZipStream gzip = new(compressed, CompressionMode.Decompress);
            using MemoryStream decompressed = new();
            gzip.CopyTo(decompressed);
            nested = DiagnosticReport.Parser.ParseFrom(decompressed.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException)
        {
            GoogleWifiCollectorLog.NestedReportParseFailed(logger, ex);
            return false;
        }
    }

    private static void AddStationFacts(List<Fact> facts, string deviceId, OnHubStation s)
    {
        string[] keys = [deviceId, s.Ip];
        facts.Add(Fact.Create(FactPaths.DiscoveredSources, keys, SourceName));

        // Obscured MAC (OUI real, device bytes obfuscated). Emitted as a raw kept fact — never as
        // DiscoveredMAC — so it is not treated as a device fingerprint. The server reconstructs it
        // by IP+OUI into DiscoveredMAC.
        facts.AddIfPresent(FactPaths.DiscoveredObscuredMAC, keys, s.Mac);
        facts.AddIfPresent(FactPaths.DiscoveredHostname, keys, s.Hostname);
        facts.AddIfPresent(FactPaths.DiscoveredFriendlyName, keys, s.FriendlyName);
        facts.AddIfPresent(FactPaths.DiscoveredModel, keys, s.Model);

        // UPnP manufacturer (the OnHub's own UPnP device-description query) — no other OnHub
        // source carries station-level vendor.
        facts.AddIfPresent(FactPaths.DiscoveredVendor, keys, s.Manufacturer);

        // Mesh points (mesh_group.node_info in networkState) otherwise look like unlabeled
        // clients — no mDNS name, no model — so tag them when mDNS didn't already supply a
        // more specific type.
        facts.AddIfPresent(FactPaths.DiscoveredDeviceType, keys, s.DeviceType ?? (s.IsMeshNode ? "OnHub Mesh Point" : null));

        // An explicit DHCP reservation means the owner configured this device in the router
        // UI — a much stronger "known, owned device" signal than a bare sighting.
        if (s.IsDhcpReserved)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredIsDhcpReserved, keys, true));
            facts.AddIfPresent(FactPaths.DiscoveredDhcpReservedIp, keys, s.DhcpReservedIp);
        }

        // Stable Cast device id — the server anchors mDNS identity to this (not IP).
        facts.AddIfPresent(FactPaths.DiscoveredCastId, keys, s.CastId);

        // Raw _googlecast TXT values, kept opaque (see FactPaths.DiscoveredCastCapabilities).
        // Capabilities is an intrinsic device property; status/running-app are transient state
        // and stay on the sighting like the other Link.* facts below.
        facts.AddIfPresent(FactPaths.DiscoveredCastCapabilities, keys, s.CastCapabilities);
        facts.AddIfPresent(FactPaths.DiscoveredLinkCastStatus, keys, s.CastStatus);
        facts.AddIfPresent(FactPaths.DiscoveredLinkCastRunningApp, keys, s.CastRunningApp);

        // Advertised services → one fact per service type (list dimension = service).
        foreach (string service in s.ServiceTypes)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredServiceName, [deviceId, s.Ip, service], service));
        }

        // ── The sighting / link (this AP's view of the client) ──
        facts.AddIfPresent(FactPaths.DiscoveredLinkMedium, keys, s.ConnectionMedium);
        facts.AddIfPresent(FactPaths.DiscoveredLinkBand, keys, s.Band);

        // Which physical mesh point relays this client (mpp dump ↔ mesh_node_info join).
        facts.AddIfPresent(FactPaths.DiscoveredLinkMeshApBssid, keys, s.MeshApBssid);

        if (s.Guest is { } guest)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkGuest, keys, guest));
        }

        if (s.SignalDbm is { } signal)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkSignalDbm, keys, signal));
        }

        if (s.TxRateMbps is { } txRate)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkTxRateMbps, keys, txRate));
        }

        if (s.RxRateMbps is { } rxRate)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkRxRateMbps, keys, rxRate));
        }

        if (s.RxBytes is { } rxBytes)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkRxBytes, keys, rxBytes));
        }

        if (s.TxBytes is { } txBytes)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkTxBytes, keys, txBytes));
        }

        if (s.ConnectedSeconds is { } connSecs)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkConnectedSeconds, keys, connSecs));
        }

        // hostapd's own STA-activity + IAPP roaming log — the only temporal signal in the
        // report (everything else above is a point-in-time snapshot).
        if (s.LastActiveAt is { } lastActiveAt)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkLastActiveAt, keys, lastActiveAt));
            facts.AddIfPresent(FactPaths.DiscoveredLinkLastActiveInterface, keys, s.LastActiveInterface);
        }

        if (s.LastRoamingAt is { } lastRoamingAt)
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredLinkLastRoamingAt, keys, lastRoamingAt));
            facts.AddIfPresent(FactPaths.DiscoveredLinkLastRoamingApIp, keys, s.LastRoamingApIp);
        }
    }

    private static void AddRouterFacts(List<Fact> facts, string deviceId, OnHubStatus status)
    {
        facts.AddIfPresent(FactPaths.HwSystemModel, [deviceId], status.System?.ModelId);

        facts.AddIfPresent(FactPaths.SystemOsVersion, [deviceId], status.Software?.SoftwareVersion);

        if (status.System?.Uptime is { } uptime && uptime > 0)
        {
            facts.Add(Fact.Create(FactPaths.SystemUptimeSeconds, [deviceId], uptime));
            // Boot time is absolute (now − uptime); emit as ISO-8601, matching OsCollector.
            string bootTime = DateTimeOffset.UtcNow.AddSeconds(-uptime).ToString("o");
            facts.Add(Fact.Create(FactPaths.SystemBootTime, [deviceId], bootTime));
        }
    }

    /// <summary>
    /// Emits the AP's own interface inventory from <c>ip -s -d addr</c>. Falls back to
    /// the two synthetic interfaces (LAN bridge from the target IP, WAN from status)
    /// when that command output is absent, so we never regress below the prior facts.
    /// </summary>
    private static void AddInterfaceFacts(
        List<Fact> facts,
        string deviceId,
        DiagnosticReport report,
        string host,
        OnHubStatus? status
    )
    {
        IReadOnlyList<OnHubInterface> interfaces = OnHubApInterfaces.Extract(report);
        if (interfaces.Count == 0)
        {
            AddSyntheticInterfaceFacts(facts, deviceId, host, status);
            return;
        }

        foreach (OnHubInterface i in interfaces)
        {
            string[] keys = [deviceId, i.Name];
            facts.Add(Fact.Create(FactPaths.InterfaceName, keys, i.Name));

            if (i.Mtu is { } mtu)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceMTU, keys, mtu));
            }

            if (i.Up is { } up)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceUp, keys, up));
            }

            facts.AddIfPresent(FactPaths.InterfaceIPv4, keys, i.Ipv4);

            if (i.Ipv4PrefixLength is { } ipv4PrefixLength)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceIPv4PrefixLength, keys, ipv4PrefixLength));
            }

            facts.AddIfPresent(FactPaths.InterfaceIPv6, keys, i.Ipv6);

            if (i.Ipv6PrefixLength is { } ipv6PrefixLength)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceIPv6PrefixLength, keys, ipv6PrefixLength));
            }

            facts.AddIfPresent(FactPaths.InterfaceType, keys, i.Type);

            if (i.SpeedBps is { } speedBps)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceSpeedBps, keys, speedBps));
            }

            facts.AddIfPresent(FactPaths.InterfaceDuplex, keys, i.Duplex);

            // Obscured MAC (real OUI, masked device bytes). Kept raw — never a
            // fingerprint. The server reconstructs the real MAC by IP + OUI.
            facts.AddIfPresent(FactPaths.InterfaceObscuredMAC, keys, i.ObscuredMac);
        }
    }

    /// <summary>
    /// Emits the AP's storage layout from <c>findmnt</c>: one filesystem per
    /// device-backed mount (mount + type; findmnt carries no sizes) and one disk per
    /// distinct underlying block device.
    /// </summary>
    private static void AddStorageFacts(List<Fact> facts, string deviceId, DiagnosticReport report)
    {
        (IReadOnlyList<OnHubFilesystem> filesystems, IReadOnlyList<string> disks) = OnHubApStorage.Extract(report);

        foreach (OnHubFilesystem fs in filesystems)
        {
            facts.AddIfPresent(FactPaths.FsType, [deviceId, fs.Mount], fs.FsType);
        }

        foreach (string disk in disks)
        {
            facts.Add(Fact.Create(FactPaths.DiskName, [deviceId, disk], disk));
        }
    }

    /// <summary>
    /// Emits bridge membership, VLAN, and spanning-tree facts already captured (unparsed) in
    /// the report's commandOutputs — no new HTTP round-trip to the AP. Any of the three source
    /// commands may be absent; each is independently optional.
    /// </summary>
    private static void AddBridgeVlanFacts(List<Fact> facts, string deviceId, DiagnosticReport report)
    {
        (
            IReadOnlyList<OnHubBridgeMembership> bridges,
            IReadOnlyList<OnHubBridgeStp> bridgeStp,
            IReadOnlyList<OnHubSwitchPort> switchPorts
        ) = OnHubApBridgeVlan.Extract(report);

        foreach (OnHubBridgeMembership bridge in bridges)
        {
            foreach (string memberInterface in bridge.MemberInterfaces)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceBridgeMaster, [deviceId, memberInterface], bridge.BridgeName));
            }
        }

        foreach (OnHubBridgeStp stp in bridgeStp)
        {
            facts.AddIfPresent(FactPaths.BridgeId, [deviceId, stp.BridgeName], stp.BridgeId);
            facts.AddIfPresent(FactPaths.BridgeRootId, [deviceId, stp.BridgeName], stp.RootId);
            if (stp.RootPathCost is { } rootPathCost)
            {
                facts.Add(Fact.Create(FactPaths.BridgeRootPathCost, [deviceId, stp.BridgeName], rootPathCost));
            }

            facts.AddIfPresent(FactPaths.BridgeRootPort, [deviceId, stp.BridgeName], stp.RootPortInterface);

            foreach (OnHubBridgePortStp port in stp.Ports)
            {
                facts.AddIfPresent(FactPaths.InterfaceStpState, [deviceId, port.Interface], port.State);
                if (port.PathCost is { } cost)
                {
                    facts.Add(Fact.Create(FactPaths.InterfaceStpCost, [deviceId, port.Interface], cost));
                }

                string? role = ComputeOnHubStpRole(port, stp);
                facts.AddIfPresent(FactPaths.InterfaceStpRole, [deviceId, port.Interface], role);
            }
        }

        // The bridges dictionary already told us whether STP is enabled at all (brctl show's
        // "STP enabled" column); the per-bridge showstp dump has no equivalent flag of its own.
        foreach (OnHubBridgeMembership bridge in bridges)
        {
            if (bridge.StpEnabled is { } stpEnabled)
            {
                facts.Add(Fact.Create(FactPaths.BridgeStpEnabled, [deviceId, bridge.BridgeName], stpEnabled));
            }
        }

        foreach (OnHubSwitchPort port in switchPorts)
        {
            string ifKey = $"switch-port-{port.Port}";
            if (port.Pvid is { } pvid)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceVlanId, [deviceId, ifKey], pvid));
            }

            if (port.TaggedVlans.Count > 0)
            {
                facts.Add(
                    Fact.Create(
                        FactPaths.InterfaceTaggedVlans,
                        [deviceId, ifKey],
                        string.Join(",", port.TaggedVlans.OrderBy(v => v))
                    )
                );
            }
        }
    }

    // Mirrors ComputeStpRole in SnmpCollector: root port wins, then designated-bridge match,
    // then blocking/disabled state — the roles a network engineer would read off
    // "show spanning-tree", derived here from brctl showstp's text fields instead of BRIDGE-MIB.
    private static string? ComputeOnHubStpRole(OnHubBridgePortStp port, OnHubBridgeStp stp)
    {
        if (string.Equals(port.Interface, stp.RootPortInterface, StringComparison.Ordinal))
        {
            return "root";
        }

        if (port.DesignatedBridge != null
         && stp.BridgeId != null
         && string.Equals(port.DesignatedBridge, stp.BridgeId, StringComparison.Ordinal))
        {
            return "designated";
        }

        return port.State switch
        {
            "disabled" => "disabled",
            "blocking" => "alternate",
            _ => null,
        };
    }

    // Prior behaviour, used only when the report lacks `ip -s -d addr`: the LAN bridge
    // IP is the target address; the WAN IP + link come from status.
    private static void AddSyntheticInterfaceFacts(
        List<Fact> facts,
        string deviceId,
        string host,
        OnHubStatus? status
    )
    {
        facts.Add(Fact.Create(FactPaths.InterfaceName, [deviceId, "br-lan"], "br-lan"));
        facts.Add(Fact.Create(FactPaths.InterfaceIPv4, [deviceId, "br-lan"], host));

        if (status?.Wan?.LocalIpAddress is { Length: > 0 } wanIp)
        {
            facts.Add(Fact.Create(FactPaths.InterfaceName, [deviceId, "wan0"], "wan0"));
            facts.Add(Fact.Create(FactPaths.InterfaceIPv4, [deviceId, "wan0"], wanIp));
            if (status.Wan.Online is { } online)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceUp, [deviceId, "wan0"], online));
            }
        }
    }

    /// <summary>
    /// Emits the AP's WAN identity from the diagnostic report's <c>networkState</c>
    /// (<c>network_service_state</c>/<c>infra_state</c>, parsed by <see cref="OnHubApWan" />).
    /// Deliberately skips fields already covered elsewhere: the negotiated WAN IPv4 and
    /// physical link speed are already emitted by <see cref="AddInterfaceFacts" /> from
    /// <c>ip -s -d addr</c>/<c>ethtool</c> — verified identical against a live capture.
    /// </summary>
    private static void AddWanFacts(List<Fact> facts, string deviceId, DiagnosticReport report)
    {
        OnHubWanDetail? wan = OnHubApWan.Extract(report.NetworkState);
        if (wan is null)
        {
            return;
        }

        string[] keys = [deviceId, wan.PrimaryInterface ?? "wan0"];
        facts.AddIfPresent(FactPaths.InterfaceConnectionType, keys, wan.ConnectionType);
        facts.AddIfPresent(FactPaths.InterfaceGateway, keys, wan.Gateway);
        facts.AddIfPresent(FactPaths.InterfaceIspType, keys, wan.IspType);

        for (int i = 0; i < wan.DnsServers.Count; i++)
        {
            facts.Add(Fact.Create(FactPaths.NetworkDnsServer, [deviceId, i.ToString()], wan.DnsServers[i]));
        }

        if (wan.SpeedTestAt is { } testAt)
        {
            facts.Add(Fact.Create(FactPaths.NetworkWanSpeedTestAt, [deviceId], testAt));

            if (wan.SpeedTestDownloadBytesPerSec is { } downBps)
            {
                facts.Add(Fact.Create(FactPaths.NetworkWanSpeedTestDownloadBps, [deviceId], downBps));
            }

            if (wan.SpeedTestUploadBytesPerSec is { } upBps)
            {
                facts.Add(Fact.Create(FactPaths.NetworkWanSpeedTestUploadBps, [deviceId], upBps));
            }

            if (wan.SpeedTestTotalDownloadedBytes is { } totalDown)
            {
                facts.Add(Fact.Create(FactPaths.NetworkWanSpeedTestTotalDownloadedBytes, [deviceId], totalDown));
            }

            if (wan.SpeedTestTotalUploadedBytes is { } totalUp)
            {
                facts.Add(Fact.Create(FactPaths.NetworkWanSpeedTestTotalUploadedBytes, [deviceId], totalUp));
            }
        }
    }

    /// <summary>AP hardware/OS facts mined from diagnostic-report command outputs.</summary>
    private static void AddApReportFacts(List<Fact> facts, string deviceId, DiagnosticReport report)
    {
        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (string.Equals(cmd.Command, "/proc/meminfo", StringComparison.Ordinal)
             && MemTotalBytes(cmd.Output) is { } memBytes)
            {
                facts.Add(Fact.Create(FactPaths.HwTotalMemBytes, [deviceId], memBytes));
            }
            else if (string.Equals(cmd.Command, "/etc/lsb-release", StringComparison.Ordinal))
            {
                AddLsbReleaseFacts(facts, deviceId, cmd.Output);
            }
        }
    }

    // Chrome OS /etc/lsb-release: distro name, full build description, and family.
    // The kernel is Linux, so Family=linux keeps it consistent with agent-collected
    // hosts (the System tab shows Distro in preference to Family).
    private static void AddLsbReleaseFacts(List<Fact> facts, string deviceId, string lsbRelease)
    {
        if (LsbValue(lsbRelease, "CHROMEOS_RELEASE_DESCRIPTION") is { Length: > 0 } build)
        {
            facts.Add(Fact.Create(FactPaths.SystemOsBuild, [deviceId], build));
        }

        if (LsbValue(lsbRelease, "CHROMEOS_RELEASE_NAME") is { Length: > 0 } distro)
        {
            facts.Add(Fact.Create(FactPaths.SystemOsDistro, [deviceId], distro));
            facts.Add(Fact.Create(FactPaths.SystemOsFamily, [deviceId], "linux"));
        }
    }

    private static string? LsbValue(string lsbRelease, string key)
    {
        foreach (string line in lsbRelease.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].Trim();
            }
        }

        return null;
    }

    private static long? MemTotalBytes(string meminfo)
    {
        foreach (string line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // "MemTotal: 490860 kB"
            if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
            {
                return kb * 1024;
            }
        }

        return null;
    }
}

internal static partial class GoogleWifiCollectorLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Google Wifi target has no address — set the AP IP as the target address; skipping."
    )]
    public static partial void MissingAddress(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Google Wifi diagnostic-report fetch failed for {Host}.")]
    public static partial void ReportFailed(ILogger logger, Exception ex, string host);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Google Wifi report for {Host} carried no device id (report field 21); cannot identify — skipping."
    )]
    public static partial void NoDeviceId(ILogger logger, string host);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Google Wifi status fetch failed for {Host}; emitting stations without router facts."
    )]
    public static partial void StatusFailed(ILogger logger, Exception ex, string host);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Google Wifi nestedReport (satellite mesh point report) failed to decompress/parse; skipping."
    )]
    public static partial void NestedReportParseFailed(ILogger logger, Exception ex);
}