using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Actively probes the local subnets so the OS ARP/ND (neighbor) cache holds an entry for every
/// live host before anything reads it (<c>ip neigh</c> / <c>arp -a</c> / <c>Get-NetNeighbor</c>).
/// Without this, the cache only holds whatever the OS happened to talk to recently, so most hosts
/// read back with no MAC — which is why ARP-derived lookups (including the server's Google
/// Wifi/OnHub obscured-MAC reconstruction, see ObscuredMac.cs) routinely miss.
///
/// Uses a per-host unicast ping sweep (System.Net.NetworkInformation.Ping) on every platform
/// rather than an OS broadcast ping: broadcast ICMP is commonly dropped by default on modern
/// Linux (icmp_echo_ignore_broadcasts) and isn't available at all via a portable API on
/// macOS/Windows, so it warmed the cache unreliably on the one platform it ran on and not at all
/// on the other two. Shared by ArpCollector (local ARP facts) and NetworkDiscoveryCollector
/// (network-discovery scanners) so both warm the same way instead of drifting apart.
///
/// Every probe is best-effort: a failure just means the cache stays less populated, never a hard
/// error, and this never blocks or fails the caller's collection.
/// </summary>
public static class NeighborCacheWarmer
{
    public readonly record struct Subnet(IPAddress Address, int PrefixLength, IPAddress LocalAddress);

    public static Task WarmAsync(CancellationToken ct) => WarmAsync(EnumerateLocalSubnets(), ct);

    public static async Task WarmAsync(IReadOnlyList<Subnet> subnets, CancellationToken ct)
    {
        foreach (Subnet subnet in subnets)
        {
            // Mirrors PingSweepScanner's PERF-017 bound: skip subnets larger than /24 rather than
            // spawn tens of thousands of pings just to warm a cache.
            if (subnet.PrefixLength < 24)
            {
                continue;
            }

            SemaphoreSlim semaphore = new(50, 50);
            List<Task> tasks = [];
            foreach (IPAddress ip in EnumerateHosts(subnet.Address, subnet.PrefixLength))
            {
                if (ip.Equals(subnet.LocalAddress))
                {
                    continue;
                }

                tasks.Add(ProbeAsync(ip, semaphore, ct));
            }

            await Task.WhenAll(tasks);
        }
    }

    private static async Task ProbeAsync(IPAddress ip, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            using Ping ping = new();
            await ping.SendPingAsync(ip, 500);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: an unreachable host just doesn't get a cache entry.
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static List<Subnet> EnumerateLocalSubnets()
    {
        List<Subnet> subnets = [];

        try
        {
            foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    // Skip link-local (169.254.x.x)
                    byte[] addrBytes = addr.Address.GetAddressBytes();
                    if (addrBytes[0] == 169 && addrBytes[1] == 254)
                    {
                        continue;
                    }

                    int prefix = GetPrefixLength(addr.IPv4Mask);
                    if (prefix < 8 || prefix > 30)
                    {
                        continue;
                    }

                    IPAddress subnetAddress = GetSubnetAddress(addr.Address, addr.IPv4Mask);
                    subnets.Add(new Subnet(subnetAddress, prefix, addr.Address));
                }
            }
        }
        catch
        {
            // Best-effort: an enumeration failure just means we warm nothing this cycle.
        }

        return subnets;
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
                [
                    (byte)(ip >> 24),
                    (byte)(ip >> 16),
                    (byte)(ip >> 8),
                    (byte)ip,
                ]
            );
        }
    }

    private static IPAddress GetSubnetAddress(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] subnet = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            subnet[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return new IPAddress(subnet);
    }

    private static int GetPrefixLength(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int count = 0;
        foreach (byte b in bytes)
        {
            byte v = b;
            while (v != 0)
            {
                count += v & 1;
                v >>= 1;
            }
        }

        return count;
    }
}