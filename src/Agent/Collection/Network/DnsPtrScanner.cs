using System.Net;
using System.Net.Sockets;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hostnames for ARP-known neighbors via reverse DNS (PTR) lookups,
/// using the OS resolver against each neighbor IP address. A successful lookup
/// (a resolvable host name) counts as confirmation. Source tag: "dns-ptr".
/// Useful as a lightweight fallback identity source for hosts that don't
/// respond to protocol-specific probes but are registered in local DNS.
/// </summary>
public sealed class DnsPtrScanner : INetworkScanner
{
    public string Name => "dns-ptr";
    public bool IsSupported => true;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<string> ips = target.Neighbors.Select(n => n.Ip.ToString()).ToList();

        SemaphoreSlim sem = new(20, 20);
        List<Task<DiscoveredDevice?>> tasks = new(ips.Count);

        foreach (string ip in ips)
        {
            tasks.Add(LookupAsync(ip, sem, ct));
        }

        DiscoveredDevice?[] results = await Task.WhenAll(tasks);

        List<DiscoveredDevice> devices = new(results.Length);
        foreach (DiscoveredDevice? d in results)
        {
            if (d is not null)
            {
                devices.Add(d);
            }
        }

        return devices;
    }

    private static async Task<DiscoveredDevice?> LookupAsync(string ip, SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));

            IPHostEntry entry = await Dns.GetHostEntryAsync(ip, linked.Token);
            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = entry.HostName,
                Source = "dns-ptr",
            };
        }
        catch (SocketException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            sem.Release();
        }
    }
}