using System.Text.Json;
using System.Text.RegularExpressions;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers Philips Hue bridges by probing HTTP(S) "/api/config" on ARP-known
/// neighbors. The presence of a "bridgeid" field in the JSON response confirms a
/// Hue bridge, from which model, firmware, API version, and MAC address are
/// extracted. Source tag: "philips-hue".
/// </summary>
public sealed class PhilipsHueScanner : UnicastScannerBase
{
    public override string Name => "philips-hue";

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
        DiscoveredDevice? device = await TryProbeAsync(ip, $"http://{ip}/api/config", ct);
        if (device is not null)
        {
            return device;
        }

        return await TryProbeAsync(ip, $"https://{ip}/api/config", ct);
    }

    private static async Task<DiscoveredDevice?> TryProbeAsync(string ip, string url, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("bridgeid", out JsonElement bridgeIdEl))
            {
                return null;
            }

            Dictionary<string, string> attributes = new();

            string? bridgeId = bridgeIdEl.GetString();
            if (!string.IsNullOrWhiteSpace(bridgeId))
            {
                attributes["hue.bridge_id"] = bridgeId;
            }

            if (root.TryGetProperty("modelid", out JsonElement modelEl))
            {
                string? model = modelEl.GetString();
                if (!string.IsNullOrWhiteSpace(model))
                {
                    attributes["hue.model"] = model;
                }
            }

            if (root.TryGetProperty("swversion", out JsonElement swVersionEl))
            {
                string? swVersion = swVersionEl.GetString();
                if (!string.IsNullOrWhiteSpace(swVersion))
                {
                    attributes["hue.version"] = swVersion;
                }
            }

            if (root.TryGetProperty("apiversion", out JsonElement apiVersionEl))
            {
                string? apiVersion = apiVersionEl.GetString();
                if (!string.IsNullOrWhiteSpace(apiVersion))
                {
                    attributes["hue.api_version"] = apiVersion;
                }
            }

            string? hostname = null;
            if (root.TryGetProperty("name", out JsonElement nameEl))
            {
                string? name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name) && name.Contains("hue", StringComparison.OrdinalIgnoreCase))
                {
                    hostname = name;
                }
            }

            if (string.IsNullOrWhiteSpace(hostname))
            {
                hostname = "Philips Hue Bridge";
            }

            string? macAddress = null;
            if (root.TryGetProperty("mac", out JsonElement macEl))
            {
                macAddress = NormalizeMac(macEl.GetString());
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                MacAddress = macAddress,
                Hostname = hostname,
                Source = "philips-hue",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeMac(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Regex.IsMatch(raw, @"^([0-9a-f]{2}:){5}[0-9a-f]{2}$"))
        {
            return raw;
        }

        string stripped = raw.Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (stripped.Length != 12)
        {
            return raw.ToLowerInvariant();
        }

        return string.Join(":", Enumerable.Range(0, 6).Select(i => stripped.Substring(i * 2, 2)));
    }
}