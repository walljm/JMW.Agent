using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers UPnP devices via SSDP M-SEARCH multicast to 239.255.255.250:1900.
/// Captures SERVER/USN/ST headers from replies, then fetches each device's
/// UPnP description XML from its LOCATION URL (restricted to the responding
/// host to guard against SSRF) to enrich the record with friendly name,
/// manufacturer, model, and device type. Useful for identifying smart TVs,
/// media servers, routers, and other UPnP-capable consumer devices.
/// Source tag: "ssdp".
/// </summary>
public sealed class SsdpScanner : INetworkScanner
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static readonly ILogger<SsdpScanner> Log = AgentLog.CreateLogger<SsdpScanner>();

    public string Name => "ssdp";

    public bool IsSupported => true;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<(IPAddress RemoteIp, string RawResponse)> rawResponses = [];

        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));

            string msearch =
                "M-SEARCH * HTTP/1.1\r\n"
              + "HOST: 239.255.255.250:1900\r\n"
              + "MAN: \"ssdp:discover\"\r\n"
              + "MX: 3\r\n"
              + "ST: ssdp:all\r\n"
              + "\r\n";

            byte[] msearchBytes = Encoding.ASCII.GetBytes(msearch);
            IPEndPoint ssdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
            await udp.SendAsync(msearchBytes, msearchBytes.Length, ssdpEndpoint);

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 4000, ct))
            {
                if (result.Buffer.Length > 8192)
                {
                    continue; // oversized SSDP response — skip
                }

                rawResponses.Add((result.RemoteEndPoint.Address, Encoding.UTF8.GetString(result.Buffer)));
            }
        }
        catch
        {
            return [];
        }

        // First pass: parse headers and queue LOCATION fetches.
        List<(string Ip, Dictionary<string, string> Attributes, string? Location)> parsed = new(rawResponses.Count);

        foreach ((IPAddress remoteIp, string rawResponse) in rawResponses)
        {
            string ip = remoteIp.ToString();
            Dictionary<string, string> headers = HttpHeaderLines.Parse(rawResponse);
            Dictionary<string, string> attributes = [];

            if (headers.TryGetValue("SERVER", out string? server)) { attributes["ssdp.server"] = server; }

            if (headers.TryGetValue("USN", out string? usn)) { attributes["ssdp.usn"] = usn; }

            if (headers.TryGetValue("ST", out string? st)) { attributes["ssdp.st"] = st; }

            string? location = null;
            if (headers.TryGetValue("LOCATION", out string? loc)
             && Uri.TryCreate(loc, UriKind.Absolute, out Uri? locationUri)
             && locationUri.Scheme == "http"
             && locationUri.Host == ip) // SSRF guard: only fetch from the responding device
            {
                location = loc;
            }

            parsed.Add((ip, attributes, location));
        }

        // Second pass: fan out all LOCATION fetches concurrently.
        Task<(string Ip, string? FriendlyName, string? Manufacturer, string? Model, string? DeviceType,
            string? PresentationUrl)>[] fetchTasks =
            parsed
                .Where(p => p.Location != null)
                .Select(p => FetchUpnpDescriptionAsync(p.Ip, p.Location!, ct))
                .ToArray();

        (string Ip, string? FriendlyName, string? Manufacturer, string? Model, string? DeviceType,
            string? PresentationUrl)[] fetchResults = await Task.WhenAll(fetchTasks);

        // Index fetch results by IP for O(1) lookup.
        Dictionary<string, (string? FriendlyName, string? Manufacturer, string? Model, string? DeviceType,
            string? PresentationUrl)> upnpByIp = new();
        foreach ((string ip, string? friendlyName, string? manufacturer, string? model, string? deviceType,
            string? presentationUrl) in fetchResults)
        {
            upnpByIp[ip] = (friendlyName, manufacturer, model, deviceType, presentationUrl);
        }

        // Third pass: merge headers + UPnP data into device map.
        Dictionary<string, DiscoveredDevice> devices = [];

        foreach ((string pIp, Dictionary<string, string> pAttributes, string? _) in parsed)
        {
            string? hostname = null;

            if (upnpByIp.TryGetValue(
                pIp,
                out (string? FriendlyName, string? Manufacturer, string? Model,
                string? DeviceType, string? PresentationUrl) upnp
            ))
            {
                if (upnp.FriendlyName != null)
                {
                    hostname = upnp.FriendlyName;
                    pAttributes["upnp.friendly_name"] = upnp.FriendlyName;
                }

                if (upnp.Manufacturer != null) { pAttributes["upnp.manufacturer"] = upnp.Manufacturer; }

                if (upnp.Model != null) { pAttributes["upnp.model"] = upnp.Model; }

                if (upnp.DeviceType != null) { pAttributes["upnp.device_type"] = upnp.DeviceType; }

                if (upnp.PresentationUrl != null) { pAttributes["upnp.presentation_url"] = upnp.PresentationUrl; }
            }

            if (!devices.TryGetValue(pIp, out DiscoveredDevice? existing))
            {
                devices[pIp] = new DiscoveredDevice
                {
                    IpAddress = pIp,
                    Hostname = hostname,
                    Source = Name,
                    Attributes = pAttributes,
                };
            }
            else
            {
                foreach (KeyValuePair<string, string> attr in pAttributes)
                {
                    existing.Attributes.TryAdd(attr.Key, attr.Value);
                }

                if (existing.Hostname == null && hostname != null)
                {
                    devices[pIp] = new DiscoveredDevice
                    {
                        IpAddress = existing.IpAddress,
                        MacAddress = existing.MacAddress,
                        Hostname = hostname,
                        Source = existing.Source,
                        Attributes = existing.Attributes,
                    };
                }
            }
        }

        return [.. devices.Values];
    }

    private static async Task<(string Ip, string? FriendlyName, string? Manufacturer, string? Model,
            string? DeviceType, string? PresentationUrl)>
        FetchUpnpDescriptionAsync(
            string ip,
            string location,
            CancellationToken ct
        )
    {
        try
        {
            string xml = await Http.GetStringAsync(location, ct);
            XDocument doc = XDocument.Parse(xml);

            string? friendlyName = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "friendlyName")
                ?.Value;
            string? manufacturer = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "manufacturer")
                ?.Value;
            string? modelName = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "modelName")
                ?.Value;
            string? deviceType = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "deviceType")
                ?.Value;
            string? presentationUrl = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "presentationURL")
                ?.Value;

            return (ip, friendlyName, manufacturer, modelName, deviceType, presentationUrl);
        }
        catch (Exception ex)
        {
            SsdpScannerLog.UpnpFetchFailed(Log, ex, location);
            return (ip, null, null, null, null, null);
        }
    }
}

internal static partial class SsdpScannerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "UPnP description fetch from {Location} failed.")]
    public static partial void UpnpFetchFailed(ILogger logger, Exception ex, string location);
}