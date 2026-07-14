using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers WS-Discovery devices via a SOAP Probe multicast to
/// 239.255.255.250:3702. Parses ProbeMatch responses for the device's
/// Address, Types, and MetadataVersion, deriving a hostname from the
/// Address when it isn't a bare IP or UUID URN. Useful for surfacing
/// network printers, scanners, and other WSD-compliant devices common on
/// Windows networks. Source tag: "ws-discovery".
/// </summary>
public sealed class WsDiscoveryScanner : INetworkScanner
{
    public string Name => "ws-discovery";
    public bool IsSupported => true;

    private static readonly IPEndPoint WsdEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    private static readonly string ProbeMessage =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
      + "<soap:Envelope"
      + " xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\""
      + " xmlns:wsa=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\""
      + " xmlns:wsd=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\">"
      + "<soap:Header>"
      + "<wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>"
      + "<wsa:MessageID>urn:uuid:00000000-0000-0000-0000-000000000001</wsa:MessageID>"
      + "<wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>"
      + "</soap:Header>"
      + "<soap:Body>"
      + "<wsd:Probe>"
      + "<wsd:Types>wsdp:Device</wsd:Types>"
      + "</wsd:Probe>"
      + "</soap:Body>"
      + "</soap:Envelope>";

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<(IPAddress RemoteIp, string ResponseXml)> rawResponses = [];

        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));

            byte[] probeBytes = Encoding.UTF8.GetBytes(ProbeMessage);
            await udp.SendAsync(probeBytes, probeBytes.Length, WsdEndpoint);

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 3000, ct))
            {
                if (result.Buffer.Length > 65536)
                {
                    continue; // oversized WS-Discovery response — skip
                }

                string xml = Encoding.UTF8.GetString(result.Buffer);
                rawResponses.Add((result.RemoteEndPoint.Address, xml));
            }
        }
        catch
        {
            return [];
        }

        Dictionary<string, DiscoveredDevice> devices = [];

        foreach ((IPAddress remoteIp, string responseXml) in rawResponses)
        {
            string ip = remoteIp.ToString();
            DiscoveredDevice? device = ParseProbeMatch(ip, responseXml);
            if (device is null)
            {
                continue;
            }

            if (!devices.TryGetValue(ip, out DiscoveredDevice? existing))
            {
                devices[ip] = device;
            }
            else
            {
                foreach (KeyValuePair<string, string> attr in device.Attributes)
                {
                    existing.Attributes.TryAdd(attr.Key, attr.Value);
                }
            }
        }

        return [.. devices.Values];
    }

    private static DiscoveredDevice? ParseProbeMatch(string ip, string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch
        {
            return null;
        }

        string? address = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Address")
            ?.Value;

        string? types = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Types")
            ?.Value;

        string? metadataVersion = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "MetadataVersion")
            ?.Value;

        if (address == null && types == null)
        {
            return null;
        }

        string? hostname = ExtractHostnameFromAddress(address);

        Dictionary<string, string> attributes = [];
        if (types != null)
        {
            attributes["wsd.types"] = types;
        }

        if (address != null)
        {
            attributes["wsd.address"] = address;
        }

        if (metadataVersion != null)
        {
            attributes["wsd.metadata_version"] = metadataVersion;
        }

        return new DiscoveredDevice
        {
            IpAddress = ip,
            Hostname = hostname,
            Source = "ws-discovery",
            Attributes = attributes,
        };
    }

    private static string? ExtractHostnameFromAddress(string? address)
    {
        if (address == null)
        {
            return null;
        }

        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host;
            if (!string.IsNullOrEmpty(host)
             && !IPAddress.TryParse(host, out IPAddress? _)
             && !host.StartsWith("uuid", StringComparison.OrdinalIgnoreCase))
            {
                return host;
            }
        }

        return null;
    }
}