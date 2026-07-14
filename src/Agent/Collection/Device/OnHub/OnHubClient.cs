using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>
/// Talks to the local, unauthenticated Google Wifi / Nest Wifi OnHub HTTP API at
/// <c>http://&lt;ap-ip&gt;/api/v1/</c>. GET-only; every request carries
/// <c>Host: localhost</c> (the firmware rejects other Host values). No TLS, no auth.
/// </summary>
public sealed class OnHubClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;

    public OnHubClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Fetches and parses the diagnostic report. The body is a gzipped protobuf
    /// (~2.8 MB, and slow — the device takes tens of seconds to produce it).
    /// </summary>
    public async Task<DiagnosticReport> GetDiagnosticReportAsync(string host, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, $"http://{host}/api/v1/diagnostic-report");
        req.Headers.Host = "localhost";

        using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        byte[] body = await resp.Content.ReadAsByteArrayAsync(ct);
        return DiagnosticReport.Parser.ParseFrom(Decompress(body));
    }

    /// <summary>
    /// Fetches the small status JSON. Returns null when the endpoint is unavailable
    /// or the body cannot be parsed — router facts are best-effort.
    /// </summary>
    public async Task<OnHubStatus?> GetStatusAsync(string host, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, $"http://{host}/api/v1/status");
        req.Headers.Host = "localhost";

        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<OnHubStatus>(s, JsonOpts, ct);
    }

    // gzip magic 0x1f 0x8b. If absent the payload is already decompressed (e.g. an
    // HttpClient with AutomaticDecompression, or a test double) — pass it through.
    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b)
        {
            return data;
        }

        using MemoryStream input = new(data);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

// ── /api/v1/status DTOs ─────────────────────────────────────────────────────────

public sealed class OnHubStatus
{
    [JsonPropertyName("system")]
    public OnHubSystemInfo? System { get; set; }

    [JsonPropertyName("software")]
    public OnHubSoftwareInfo? Software { get; set; }

    [JsonPropertyName("wan")]
    public OnHubWanInfo? Wan { get; set; }

    [JsonPropertyName("setupState")]
    public string? SetupState { get; set; }
}

public sealed class OnHubSystemInfo
{
    [JsonPropertyName("hardwareId")]
    public string? HardwareId { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    // uptime / countryCode / groupRole are nested under "system" in the real payload.
    [JsonPropertyName("uptime")]
    public long? Uptime { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("groupRole")]
    public string? GroupRole { get; set; }
}

public sealed class OnHubSoftwareInfo
{
    [JsonPropertyName("softwareVersion")]
    public string? SoftwareVersion { get; set; }
}

public sealed class OnHubWanInfo
{
    [JsonPropertyName("localIpAddress")]
    public string? LocalIpAddress { get; set; }

    [JsonPropertyName("gatewayIpAddress")]
    public string? GatewayIpAddress { get; set; }

    [JsonPropertyName("online")]
    public bool? Online { get; set; }
}