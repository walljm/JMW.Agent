using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers CoAP devices on UDP port 5683 via a "/.well-known/core" resource
/// discovery request, first as an "all CoAP nodes" multicast (224.0.1.187) and
/// then as unicast probes against any ARP-known neighbors not already found.
/// Any parsable CoAP response is treated as confirmation. Source tag: "coap".
/// Useful for surfacing constrained IoT/smart-home devices that speak CoAP
/// instead of HTTP.
/// </summary>
public sealed class CoApScanner : INetworkScanner
{
    public string Name => "coap";
    public bool IsSupported => true;

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.1.187");
    private const int CoApPort = 5683;

    private static readonly byte[] DiscoveryRequest = new byte[]
    {
        0x41, // Ver=1, T=0 (CON), TKL=1
        0x01, // Code: GET
        0x00,
        0x01, // Message ID
        0xAB, // Token
        0xBB, // Option: delta=11 (Uri-Path), length=11
        0x2E,
        0x77,
        0x65,
        0x6C,
        0x6C,
        0x2D,
        0x6B,
        0x6E,
        0x6F,
        0x77,
        0x6E, // ".well-known"
        0x04, // Option: delta=0 (same), length=4
        0x63,
        0x6F,
        0x72,
        0x65, // "core"
    };

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        Dictionary<string, DiscoveredDevice> seen = new();

        await ProbeMulticastAsync(target, seen, ct);

        List<IPAddress> neighbors = target.Neighbors.Select(n => n.Ip).ToList();
        await ProbeUnicastAsync(target, neighbors, seen, ct);

        return [.. seen.Values];
    }

    // ── Multicast probe ───────────────────────────────────────────────────────

    private static async Task ProbeMulticastAsync(
        NetworkScanTarget target,
        Dictionary<string, DiscoveredDevice> seen,
        CancellationToken ct
    )
    {
        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));

            IPEndPoint multicastEndpoint = new(MulticastGroup, CoApPort);
            await udp.SendAsync(DiscoveryRequest, DiscoveryRequest.Length, multicastEndpoint);

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 3000, ct))
            {
                string sourceIp = result.RemoteEndPoint.Address.ToString();

                if (seen.ContainsKey(sourceIp))
                {
                    continue;
                }

                DiscoveredDevice? device = ParseResponse(result.Buffer, sourceIp);
                if (device is not null)
                {
                    seen[sourceIp] = device;
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    // ── Unicast probes against known neighbors ────────────────────────────────

    private static async Task ProbeUnicastAsync(
        NetworkScanTarget target,
        List<IPAddress> neighbors,
        Dictionary<string, DiscoveredDevice> seen,
        CancellationToken ct
    )
    {
        SemaphoreSlim semaphore = new(20, 20);
        List<Task> tasks = new();

        foreach (IPAddress neighbor in neighbors)
        {
            if (seen.ContainsKey(neighbor.ToString()))
            {
                continue;
            }

            IPAddress captured = neighbor;
            tasks.Add(ProbeUnicastOneAsync(target, captured, seen, semaphore, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task ProbeUnicastOneAsync(
        NetworkScanTarget target,
        IPAddress ip,
        Dictionary<string, DiscoveredDevice> seen,
        SemaphoreSlim semaphore,
        CancellationToken ct
    )
    {
        await semaphore.WaitAsync(ct);
        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));

            IPEndPoint endpoint = new(ip, CoApPort);
            await udp.SendAsync(DiscoveryRequest, DiscoveryRequest.Length, endpoint);

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(2000);
                UdpReceiveResult result = await udp.ReceiveAsync(timeout.Token);
                string sourceIp = result.RemoteEndPoint.Address.ToString();

                DiscoveredDevice? device = ParseResponse(result.Buffer, sourceIp);
                if (device is not null)
                {
                    lock (seen)
                    {
                        seen.TryAdd(sourceIp, device);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // no response within timeout
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ── CoAP response parsing ─────────────────────────────────────────────────

    private static DiscoveredDevice? ParseResponse(byte[] data, string sourceIp)
    {
        if (data.Length < 4)
        {
            return null;
        }

        byte code = data[1];
        if (code != 0x45 && code != 0x44)
        {
            return null;
        }

        byte tkl = (byte)(data[0] & 0x0F);
        int offset = 4 + tkl;

        // skip options to find payload marker (0xFF)
        while (offset < data.Length && data[offset] != 0xFF)
        {
            if (data[offset] == 0xFF)
            {
                break;
            }

            byte optHeader = data[offset++];
            int delta = (optHeader >> 4) & 0x0F;
            int length = optHeader & 0x0F;

            if (delta == 13)
            {
                if (offset >= data.Length)
                {
                    return null;
                }

                offset++;
            }
            else if (delta == 14)
            {
                if (offset + 1 >= data.Length)
                {
                    return null;
                }

                offset += 2;
            }

            if (length == 13)
            {
                if (offset >= data.Length)
                {
                    return null;
                }

                length = data[offset++] + 13;
            }
            else if (length == 14)
            {
                if (offset + 1 >= data.Length)
                {
                    return null;
                }

                length = ((data[offset] << 8) | data[offset + 1]) + 269;
                offset += 2;
            }

            offset += length;
        }

        string payload = "";
        if (offset < data.Length && data[offset] == 0xFF)
        {
            payload = Encoding.UTF8.GetString(data, offset + 1, data.Length - offset - 1);
        }

        (List<string> paths, List<string> types) = ParseCoreLinkFormat(payload);

        Dictionary<string, string> attributes = new();

        if (paths.Count > 0)
        {
            attributes["coap.resources"] = string.Join(",", paths);
        }

        if (types.Count > 0)
        {
            attributes["coap.types"] = string.Join(",", types);
        }

        return new DiscoveredDevice
        {
            IpAddress = sourceIp,
            Source = "coap",
            Attributes = attributes,
        };
    }

    private static (List<string> paths, List<string> types) ParseCoreLinkFormat(string payload)
    {
        List<string> paths = new();
        List<string> types = new();

        foreach (string entry in payload.Split(','))
        {
            string trimmed = entry.Trim();
            int pathEnd = trimmed.IndexOf('>');
            if (pathEnd > 1 && trimmed.StartsWith('<'))
            {
                paths.Add(trimmed[1..pathEnd]);
            }

            foreach (string param in trimmed.Split(';').Skip(1))
            {
                if (param.StartsWith("rt=", StringComparison.OrdinalIgnoreCase))
                {
                    types.Add(param[3..].Trim('"'));
                }
            }
        }

        return (paths, types);
    }
}