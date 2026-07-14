namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>
/// The AP's WAN-side configuration and status, distilled from the diagnostic report's
/// <c>networkState</c> blob (field 16) — specifically its <c>network_service_state</c> and
/// <c>infra_state</c> siblings. Richer than <see cref="OnHubClient" />'s <c>/status</c> JSON
/// WAN DTO (IP/gateway/online only): this also carries connection type, PPPoE username,
/// negotiated link speed, ISP-provided DNS servers, and the last on-device speed test.
/// </summary>
public sealed record OnHubWanDetail(
    string? ConnectionType, // raw device token: "DHCP" | "STATIC" | "PPPOE" | ...
    string? StaticIpAddress,
    string? StaticNetmask,
    string? StaticGateway,
    string? PppoeUsername,
    string? IpAddress, // wan_state — the live-negotiated address (populated regardless of ConnectionType)
    string? Gateway,
    long? LinkSpeedMbps,
    string? PrimaryInterface,
    string? IspType, // raw device token, e.g. "ISP_NONE"
    IReadOnlyList<string> DnsServers, // ISP-provided WAN-side DNS (wan_name_servers) — distinct from any LAN-configured custom DNS
    DateTimeOffset? SpeedTestAt,
    long? SpeedTestDownloadBytesPerSec,
    long? SpeedTestUploadBytesPerSec,
    long? SpeedTestTotalDownloadedBytes,
    long? SpeedTestTotalUploadedBytes
);

/// <summary>
/// Parses <see cref="OnHubWanDetail" /> out of the diagnostic report's <c>networkState</c> text
/// blob. <c>network_service_state</c> and <c>infra_state</c> are top-level siblings within that
/// one blob (verified against a live capture) — same parser (<see cref="OnHubTextFormat" />) and
/// lenient-degrade philosophy as <see cref="OnHubApInterfaces" />/<see cref="OnHubStations" />.
/// </summary>
public static class OnHubApWan
{
    public static OnHubWanDetail? Extract(string networkState)
    {
        if (string.IsNullOrEmpty(networkState))
        {
            return null;
        }

        IReadOnlyList<TextNode> roots = OnHubTextFormat.Parse(networkState);
        TextNode? netSvc = roots.FirstOrDefault(n => string.Equals(n.Name, "network_service_state", StringComparison.Ordinal));
        TextNode? infra = roots.FirstOrDefault(n => string.Equals(n.Name, "infra_state", StringComparison.Ordinal));

        if (netSvc is null && infra is null)
        {
            return null;
        }

        TextNode? wanConfig = netSvc?.ChildrenNamed("wan_configuration").FirstOrDefault();
        TextNode? wanState = netSvc?.ChildrenNamed("wan_state").FirstOrDefault();
        TextNode? staticConfig = wanConfig?.ChildrenNamed("static_configuration").FirstOrDefault();
        TextNode? pppoeConfig = wanConfig?.ChildrenNamed("pppoe_configuration").FirstOrDefault();
        TextNode? wanNameServers = netSvc?.ChildrenNamed("wan_name_servers").FirstOrDefault();
        TextNode? ispConfig = infra?.ChildrenNamed("isp_configuration").FirstOrDefault();
        TextNode? speedTest = infra?.ChildrenNamed("wan_speed_test_results").FirstOrDefault();

        List<string> dnsServers = wanNameServers?.ChildrenNamed("dns_server")
            .Select(n => n.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList() ?? [];

        return new OnHubWanDetail(
            ConnectionType: NullIfEmpty(wanConfig?.ScalarOf("type")),
            StaticIpAddress: NullIfEmpty(staticConfig?.ScalarOf("ip_address")),
            StaticNetmask: NullIfEmpty(staticConfig?.ScalarOf("netmask")),
            StaticGateway: NullIfEmpty(staticConfig?.ScalarOf("gateway")),
            PppoeUsername: NullIfEmpty(pppoeConfig?.ScalarOf("username")),
            IpAddress: NullIfEmpty(wanState?.ScalarOf("ip_address")),
            Gateway: NullIfEmpty(wanState?.ScalarOf("gateway_address")),
            LinkSpeedMbps: OnHubTextFormat.ParseLong(wanState?.ScalarOf("link_speed_mbps") ?? string.Empty),
            PrimaryInterface: NullIfEmpty(wanState?.ScalarOf("primary_wan_interface")),
            IspType: NullIfEmpty(ispConfig?.ScalarOf("isp_type")),
            DnsServers: dnsServers,
            SpeedTestAt: ParseEpochSeconds(speedTest?.ScalarOf("date_time_seconds_since_epoch")),
            SpeedTestDownloadBytesPerSec: OnHubTextFormat.ParseLong(speedTest?.ScalarOf("download_speed_bytes_per_second") ?? string.Empty),
            SpeedTestUploadBytesPerSec: OnHubTextFormat.ParseLong(speedTest?.ScalarOf("upload_speed_bytes_per_second") ?? string.Empty),
            SpeedTestTotalDownloadedBytes: OnHubTextFormat.ParseLong(speedTest?.ScalarOf("total_bytes_downloaded") ?? string.Empty),
            SpeedTestTotalUploadedBytes: OnHubTextFormat.ParseLong(speedTest?.ScalarOf("total_bytes_uploaded") ?? string.Empty)
        );
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static DateTimeOffset? ParseEpochSeconds(string? s) =>
        OnHubTextFormat.ParseLong(s ?? string.Empty) is { } secs && secs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : null;
}
