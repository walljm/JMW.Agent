using System.Net;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Scans a local subnet using a specific discovery protocol and returns
/// the devices it found. Unlike IDeviceCollector (which collects from a known
/// target), a network scanner broadcasts or multicasts to find unknown devices.
/// Each scanner covers one protocol. The agent runs all supported scanners
/// across every local subnet and merges the results per IP address.
/// </summary>
public interface INetworkScanner
{
    /// <summary>Short name for logging. e.g. "mdns", "arp", "ssdp".</summary>
    string Name { get; }

    /// <summary>
    /// Returns false if this scanner cannot run on the current OS or
    /// if required system capabilities are unavailable.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Scan the given subnet and return all discovered devices.
    /// May return the same IP multiple times across scanner instances —
    /// the caller merges by IP.
    /// </summary>
    Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct);
}

/// <summary>
/// Describes the local subnet to scan: the subnet address, prefix length,
/// and the IP address the agent host has on that subnet (used as the source
/// address for unicast probes and for exclusion from the result set).
/// </summary>
public sealed class NetworkScanTarget
{
    public required IPAddress SubnetAddress { get; init; }
    public required int PrefixLength { get; init; }

    /// <summary>The agent host's own IP on this interface — excluded from results.</summary>
    public required IPAddress LocalAddress { get; init; }

    /// <summary>Network interface name (e.g. "eth0", "en0"). Used for binding.</summary>
    public string? InterfaceName { get; init; }

    /// <summary>
    /// On-link neighbors for this subnet, resolved once per cycle by the collector (the host's own
    /// address already excluded). Scanners probe these instead of each re-reading the ARP/ND table.
    /// See <see cref="NeighborTable" /> (review D1).
    /// </summary>
    public IReadOnlyList<Neighbor> Neighbors { get; init; } = [];

    /// <summary>
    /// True when <paramref name="ip" /> falls within this target's subnet. The single home
    /// for the subnet-membership math that was previously copy-pasted as <c>IsInSubnet</c> across the
    /// scanners (review D1).
    /// </summary>
    public bool Contains(IPAddress ip)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] subnetBytes = SubnetAddress.GetAddressBytes();
        if (ipBytes.Length != subnetBytes.Length)
        {
            return false;
        }

        int maskBits = PrefixLength;
        for (int i = 0; i < subnetBytes.Length; i++)
        {
            int bits = Math.Min(maskBits, 8);
            byte mask = bits == 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
            if ((ipBytes[i] & mask) != (subnetBytes[i] & mask))
            {
                return false;
            }

            maskBits -= bits;
        }

        return true;
    }
}

/// <summary>A host neighbor-table entry: an on-link IP and its MAC (null when the OS did not report one).</summary>
public sealed record Neighbor(IPAddress Ip, string? Mac);

/// <summary>
/// A device found by a network scanner.
/// All fields are best-effort — a scanner may only fill in IP and Source.
/// The agent merges multiple DiscoveredDevice records for the same IP address
/// before sending them to the server.
/// </summary>
public sealed class DiscoveredDevice
{
    /// <summary>IP address of the discovered device. Always set.</summary>
    public required string IpAddress { get; init; }

    /// <summary>MAC address, if discoverable by the scanner.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Hostname or mDNS service name, if available.</summary>
    public string? Hostname { get; init; }

    /// <summary>Which scanner found this device. e.g. "mdns", "arp".</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Protocol-specific attributes discovered during scanning.
    /// e.g. "mdns.service._http._tcp.local", "ssdp.server_header", "upnp.friendly_name"
    /// </summary>
    public Dictionary<string, string> Attributes { get; init; } = [];
}