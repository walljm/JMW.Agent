using System.Text.Json;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers Google Cast (Chromecast) devices by probing the "eureka_info"
/// endpoint on port 8008 (falling back to 8443 over HTTPS) on ARP-known
/// neighbors. Parses the JSON response for friendly name, model, firmware
/// build, and connected SSID. Source tag: "eureka".
/// </summary>
public sealed class EurekaScanner : UnicastScannerBase
{
    public override string Name => "eureka";

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
        DiscoveredDevice? device = await TryProbeAsync(ip, $"http://{ip}:8008/setup/eureka_info", ct);
        if (device is not null)
        {
            return device;
        }

        return await TryProbeAsync(ip, $"https://{ip}:8443/setup/eureka_info", ct);
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

            Dictionary<string, string> attributes = new();
            string? hostname = null;

            if (root.TryGetProperty("device_info", out JsonElement deviceInfo))
            {
                if (deviceInfo.TryGetProperty("friendly_name", out JsonElement friendlyName))
                {
                    hostname = friendlyName.GetString();
                }

                if (deviceInfo.TryGetProperty("model_name", out JsonElement modelName))
                {
                    string? model = modelName.GetString();
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        attributes["eureka.model"] = model;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(hostname) && root.TryGetProperty("name", out JsonElement nameEl))
            {
                hostname = nameEl.GetString();
            }

            if (root.TryGetProperty("build_version", out JsonElement buildVersion))
            {
                string? ver = buildVersion.GetString();
                if (!string.IsNullOrWhiteSpace(ver))
                {
                    attributes["eureka.version"] = ver;
                }
            }

            if (root.TryGetProperty("cast_build_revision", out JsonElement castRevision))
            {
                string? castVer = castRevision.GetString();
                if (!string.IsNullOrWhiteSpace(castVer))
                {
                    attributes["eureka.cast_version"] = castVer;
                }
            }

            if (root.TryGetProperty("ssid", out JsonElement ssid))
            {
                string? ssidVal = ssid.GetString();
                if (!string.IsNullOrWhiteSpace(ssidVal))
                {
                    attributes["eureka.ssid"] = ssidVal;
                }
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Source = "eureka",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }
}