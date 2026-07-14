using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers Roku devices by probing port 8060 on ARP-known neighbors.
/// Issues an HTTP GET to /query/device-info (Roku's ECP API) and parses the
/// XML response for model, serial number, software version, and Wi-Fi MAC.
/// Source tag: "roku".
/// </summary>
public sealed class RokuScanner : UnicastScannerBase
{
    public override string Name => "roku";

    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await Http.GetAsync($"http://{ip}:8060/query/device-info", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            XDocument doc = XDocument.Parse(body);
            XElement? root = doc.Root;
            if (root is null)
            {
                return null;
            }

            Dictionary<string, string> attributes = new();
            string? hostname = root.Element("friendly-device-name")?.Value;
            string? modelName = root.Element("friendly-model-name")?.Value;
            string? modelNumber = root.Element("model-number")?.Value;
            string? version = root.Element("software-version")?.Value;
            string? serial = root.Element("serial-number")?.Value;
            string? mac = root.Element("wifi-mac")?.Value;

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                attributes["roku.model"] = modelName;
            }

            if (!string.IsNullOrWhiteSpace(modelNumber))
            {
                attributes["roku.model_number"] = modelNumber;
            }

            if (!string.IsNullOrWhiteSpace(version))
            {
                attributes["roku.version"] = version;
            }

            if (!string.IsNullOrWhiteSpace(serial))
            {
                attributes["roku.serial"] = serial;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                MacAddress = string.IsNullOrWhiteSpace(mac) ? null : mac,
                Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Source = "roku",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }
}