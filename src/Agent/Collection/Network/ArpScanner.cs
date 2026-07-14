namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Emits a <see cref="DiscoveredDevice" /> for every on-link neighbor that has a known MAC. The
/// neighbor table is resolved once per cycle by the collector and handed in via
/// <see cref="NetworkScanTarget.Neighbors" /> (review D1) — this scanner no longer reads the ARP
/// table or warms the cache itself; both now live in the collector.
/// </summary>
public sealed class ArpScanner : INetworkScanner
{
    public string Name => "arp";
    public bool IsSupported => true;

    public Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<DiscoveredDevice> devices = new();

        foreach (Neighbor neighbor in target.Neighbors)
        {
            if (neighbor.Mac is not { Length: > 0 } mac)
            {
                continue;
            }

            devices.Add(
                new DiscoveredDevice
                {
                    IpAddress = neighbor.Ip.ToString(),
                    MacAddress = mac,
                    Source = "arp",
                }
            );
        }

        return Task.FromResult<IReadOnlyList<DiscoveredDevice>>(devices);
    }
}