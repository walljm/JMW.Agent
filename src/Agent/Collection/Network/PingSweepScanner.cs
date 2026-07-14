using System.Net;
using System.Net.NetworkInformation;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts via a plain ICMP echo (ping) sweep of every address in the
/// target subnet, skipping subnets larger than /24 to avoid spawning excessive
/// concurrent tasks. Acts as a protocol-agnostic fallback that catches devices
/// which don't respond to any of the other protocol-specific scanners.
/// Source tag: "ping-sweep".
/// </summary>
public sealed class PingSweepScanner : INetworkScanner
{
    public string Name => "ping-sweep";
    public bool IsSupported => true;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<DiscoveredDevice> devices = new();

        // PERF-017: skip subnets larger than /24 (more than 254 hosts would spawn 65K+ tasks).
        if (target.PrefixLength < 24)
        {
            return devices;
        }

        SemaphoreSlim semaphore = new(50, 50);
        List<Task<DiscoveredDevice?>> tasks = new();

        foreach (IPAddress ip in EnumerateHosts(target.SubnetAddress, target.PrefixLength))
        {
            if (ip.Equals(target.LocalAddress))
            {
                continue;
            }

            IPAddress captured = ip;
            tasks.Add(ProbeAsync(captured, semaphore, ct));
        }

        DiscoveredDevice?[] results = await Task.WhenAll(tasks);

        foreach (DiscoveredDevice? device in results)
        {
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    private static async Task<DiscoveredDevice?> ProbeAsync(IPAddress ip, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(ip, 1000);
            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            return new DiscoveredDevice
            {
                IpAddress = reply.Address.ToString(),
                Source = "ping-sweep",
            };
        }
        catch (PingException)
        {
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static IEnumerable<IPAddress> EnumerateHosts(IPAddress subnet, int prefix)
    {
        if (prefix < 16)
        {
            yield break;
        }

        byte[] b = subnet.GetAddressBytes();
        uint start = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint count = 1u << (32 - prefix);

        for (uint i = 1; i < count - 1; i++)
        {
            uint ip = start + i;
            yield return new IPAddress(
                new[]
                {
                    (byte)(ip >> 24),
                    (byte)(ip >> 16),
                    (byte)(ip >> 8),
                    (byte)ip,
                }
            );
        }
    }
}