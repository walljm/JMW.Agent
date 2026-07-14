using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts via mDNS (Bonjour) service enumeration: joins the 224.0.0.251
/// multicast group and queries "_services._dns-sd._udp.local" on port 5353, then
/// parses A/PTR/TXT records from the responses to recover hostnames, advertised
/// services, and TXT metadata. Useful for identifying Apple devices, printers,
/// and other IoT/mDNS-aware equipment. Source tag: "mdns".
/// </summary>
public sealed class MdnsScanner : INetworkScanner
{
    public string Name => "mdns";

    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<byte[]> packets = [];

        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));
            udp.MulticastLoopback = false;
            udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), target.LocalAddress);

            byte[] query = BuildPtrQuery("_services._dns-sd._udp.local");
            IPEndPoint mdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);
            await udp.SendAsync(query, query.Length, mdnsEndpoint);

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 3000, ct))
            {
                packets.Add(result.Buffer);
            }
        }
        catch
        {
            return [];
        }

        Dictionary<string, string> ipToHostname = [];
        Dictionary<string, List<string>> ipToServices = [];
        Dictionary<string, Dictionary<string, string>> ipToTxt = [];

        foreach (byte[] packet in packets)
        {
            ParseDnsResponse(packet, ipToHostname, ipToServices, ipToTxt);
        }

        List<DiscoveredDevice> devices = [];

        foreach (KeyValuePair<string, string> entry in ipToHostname)
        {
            string ip = entry.Key;
            string hostname = entry.Value;

            Dictionary<string, string> attributes = [];

            if (ipToServices.TryGetValue(ip, out List<string>? services) && services.Count > 0)
            {
                attributes["mdns.services"] = string.Join(",", services);
            }

            if (ipToTxt.TryGetValue(ip, out Dictionary<string, string>? txtRecords))
            {
                foreach (KeyValuePair<string, string> txt in txtRecords)
                {
                    attributes[$"mdns.txt.{txt.Key}"] = txt.Value;
                }
            }

            devices.Add(
                new DiscoveredDevice
                {
                    IpAddress = ip,
                    Hostname = hostname,
                    Source = Name,
                    Attributes = attributes,
                }
            );
        }

        return devices;
    }

    private static byte[] BuildPtrQuery(string name)
    {
        List<byte> packet = [];

        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x01]);
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x00]);
        packet.AddRange([0x00, 0x00]);

        foreach (string label in name.Split('.'))
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)labelBytes.Length);
            packet.AddRange(labelBytes);
        }

        packet.Add(0x00);

        packet.AddRange([0x00, 0x0C]);
        packet.AddRange([0x80, 0x01]);

        return [.. packet];
    }

    private static void ParseDnsResponse(
        byte[] packet,
        Dictionary<string, string> ipToHostname,
        Dictionary<string, List<string>> ipToServices,
        Dictionary<string, Dictionary<string, string>> ipToTxt
    )
    {
        if (packet.Length < 12)
        {
            return;
        }

        int qdCount = (packet[4] << 8) | packet[5];
        int anCount = (packet[6] << 8) | packet[7];

        int offset = 12;

        for (int i = 0; i < qdCount && offset < packet.Length; i++)
        {
            DnsWire.SkipName(packet, ref offset);
            offset += 4;
        }

        for (int i = 0; i < anCount && offset < packet.Length; i++)
        {
            DnsWire.SkipName(packet, ref offset);

            if (offset + 10 > packet.Length)
            {
                break;
            }

            int type = (packet[offset] << 8) | packet[offset + 1];
            offset += 4;
            offset += 4;
            int rdLength = (packet[offset] << 8) | packet[offset + 1];
            offset += 2;

            if (offset + rdLength > packet.Length)
            {
                break;
            }

            if (type == 1 && rdLength == 4)
            {
                string ip = $"{packet[offset]}.{packet[offset + 1]}.{packet[offset + 2]}.{packet[offset + 3]}";
                if (!ipToHostname.ContainsKey(ip))
                {
                    ipToHostname[ip] = ip;
                }
            }
            else if (type == 12)
            {
                int nameOffset = offset;
                string ptrName = DnsWire.ReadName(packet, ref nameOffset);

                string[] parts = ptrName.Split('.');
                if (parts.Length >= 4)
                {
                    string serviceType = string.Join(".", parts[^4..^1]);
                    string host = parts[^1];

                    if (!ipToServices.TryGetValue(host, out List<string>? hostServices))
                    {
                        hostServices = [];
                        ipToServices[host] = hostServices;
                    }

                    hostServices.Add(serviceType);
                }
            }
            else if (type == 16)
            {
                int txtOffset = offset;
                int end = offset + rdLength;
                Dictionary<string, string> txtPairs = [];

                while (txtOffset < end)
                {
                    int strLen = packet[txtOffset++];
                    if (txtOffset + strLen > end)
                    {
                        break;
                    }

                    string str = Encoding.UTF8.GetString(packet, txtOffset, strLen);
                    txtOffset += strLen;

                    int eqIdx = str.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        txtPairs[str[..eqIdx]] = str[(eqIdx + 1)..];
                    }
                }

                foreach (KeyValuePair<string, string> kv in txtPairs)
                {
                    foreach (string ip in ipToHostname.Keys)
                    {
                        if (!ipToTxt.TryGetValue(ip, out Dictionary<string, string>? ipTxt))
                        {
                            ipTxt = [];
                            ipToTxt[ip] = ipTxt;
                        }

                        ipTxt[kv.Key] = kv.Value;
                    }
                }
            }

            offset += rdLength;
        }
    }
}