using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers AirPlay-capable devices by probing port 7000 on ARP-known neighbors.
/// Requests the device's /info endpoint and parses the returned property list
/// (binary or XML plist) for model and name, confirming AirPlay support by the
/// presence of a valid plist response. Source tag: "airplay".
/// Surfaces Apple TVs, HomePods, and AirPlay-receiver speakers/displays.
/// </summary>
public sealed class AirPlayScanner : UnicastScannerBase
{
    public override string Name => "airplay";

    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static readonly Regex MacPattern = new(
        @"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$",
        RegexOptions.Compiled
    );

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await Http.GetAsync($"http://{ip}:7000/info", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bodyBytes.Length == 0)
            {
                return null;
            }

            bool isBinaryPlist = bodyBytes.Length >= 6
             && bodyBytes[0] == 'b'
             && bodyBytes[1] == 'p'
             && bodyBytes[2] == 'l'
             && bodyBytes[3] == 'i'
             && bodyBytes[4] == 's'
             && bodyBytes[5] == 't';

            if (isBinaryPlist)
            {
                return new DiscoveredDevice
                {
                    IpAddress = ip,
                    Source = "airplay",
                    Attributes = new Dictionary<string, string>
                    {
                        ["airplay.plist_format"] = "binary",
                    },
                };
            }

            string body = Encoding.UTF8.GetString(bodyBytes);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(body);
            }
            catch
            {
                return null;
            }

            XElement? plistRoot = doc.Root;
            if (plistRoot is null || plistRoot.Name.LocalName != "plist")
            {
                return null;
            }

            XElement? dict = plistRoot.Element("dict");
            if (dict is null)
            {
                return null;
            }

            Dictionary<string, string> plistValues = ParsePlistDict(dict);

            Dictionary<string, string> attributes = new();
            string? hostname = plistValues.GetValueOrDefault("name");

            if (plistValues.TryGetValue("model", out string? model) && !string.IsNullOrWhiteSpace(model))
            {
                attributes["airplay.model"] = model;
            }

            if (plistValues.TryGetValue("osVersion", out string? osVersion) && !string.IsNullOrWhiteSpace(osVersion))
            {
                attributes["airplay.version"] = osVersion;
            }

            if (plistValues.TryGetValue("features", out string? features) && !string.IsNullOrWhiteSpace(features))
            {
                attributes["airplay.features"] = features;
            }

            string? mac = null;
            if (plistValues.TryGetValue("deviceID", out string? deviceId) && !string.IsNullOrWhiteSpace(deviceId))
            {
                if (MacPattern.IsMatch(deviceId))
                {
                    mac = deviceId;
                }
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                MacAddress = mac,
                Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Source = "airplay",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParsePlistDict(XElement dict)
    {
        Dictionary<string, string> result = new();
        List<XElement> children = dict.Elements().ToList();
        for (int i = 0; i + 1 < children.Count; i += 2)
        {
            if (children[i].Name.LocalName == "key")
            {
                result[children[i].Value] = children[i + 1].Value;
            }
        }

        return result;
    }
}