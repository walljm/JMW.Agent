using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts by sending LLMNR (Link-Local Multicast Name Resolution) PTR
/// queries to 224.0.0.252:5355 for each ARP-known neighbor IP. Windows hosts that
/// support LLMNR — the fallback name resolution used when DNS is unavailable —
/// reply with their hostname. Source tag: "llmnr".
/// </summary>
public sealed class LlmnrScanner : INetworkScanner
{
    public string Name => "llmnr";
    public bool IsSupported => true;

    private static readonly IPAddress LlmnrMulticast = IPAddress.Parse("224.0.0.252");
    private const int LlmnrPort = 5355;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<string> ips = target.Neighbors.Select(n => n.Ip.ToString()).ToList();

        if (ips.Count == 0)
        {
            return [];
        }

        Dictionary<string, string> responsePtrNames = [];

        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));

            ushort id = 1;
            foreach (string ip in ips)
            {
                byte[] query = BuildPtrQuery(ip, id++);
                await udp.SendAsync(query, query.Length, new IPEndPoint(LlmnrMulticast, LlmnrPort));
            }

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 2000, ct))
            {
                string? ptrName = ParsePtrResponse(result.Buffer);
                if (ptrName != null)
                {
                    responsePtrNames.TryAdd(result.RemoteEndPoint.Address.ToString(), ptrName);
                }
            }
        }
        catch
        {
            return [];
        }

        List<DiscoveredDevice> devices = [];
        foreach (KeyValuePair<string, string> entry in responsePtrNames)
        {
            devices.Add(
                new DiscoveredDevice
                {
                    IpAddress = entry.Key,
                    Hostname = entry.Value.TrimEnd('.'),
                    Source = "llmnr",
                }
            );
        }

        return devices;
    }

    private static byte[] BuildPtrQuery(string ip, ushort id)
    {
        string[] parts = ip.Split('.');
        string reverseName = $"{parts[3]}.{parts[2]}.{parts[1]}.{parts[0]}.in-addr.arpa";

        List<byte> packet = [];

        packet.Add((byte)(id >> 8));
        packet.Add((byte)(id & 0xFF));
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x01]);
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x00]);

        foreach (string label in reverseName.Split('.'))
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)labelBytes.Length);
            packet.AddRange(labelBytes);
        }

        packet.Add(0x00);

        packet.AddRange([0x00, 0x0C]);
        packet.AddRange([0x00, 0x01]);

        return [.. packet];
    }

    private static string? ParsePtrResponse(byte[] data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        int flags = (data[2] << 8) | data[3];
        bool isResponse = (flags & 0x8000) != 0;
        if (!isResponse)
        {
            return null;
        }

        int anCount = (data[6] << 8) | data[7];
        if (anCount == 0)
        {
            return null;
        }

        int qdCount = (data[4] << 8) | data[5];
        int offset = 12;

        for (int i = 0; i < qdCount && offset < data.Length; i++)
        {
            DnsWire.SkipName(data, ref offset);
            offset += 4;
        }

        for (int i = 0; i < anCount && offset < data.Length; i++)
        {
            DnsWire.SkipName(data, ref offset);

            if (offset + 10 > data.Length)
            {
                break;
            }

            int type = (data[offset] << 8) | data[offset + 1];
            offset += 4;
            offset += 4;
            int rdLength = (data[offset] << 8) | data[offset + 1];
            offset += 2;

            if (offset + rdLength > data.Length)
            {
                break;
            }

            if (type == 12)
            {
                int nameOffset = offset;
                return DnsWire.ReadName(data, ref nameOffset);
            }

            offset += rdLength;
        }

        return null;
    }
}