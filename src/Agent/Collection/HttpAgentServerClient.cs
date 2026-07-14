using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// HTTP implementation of IAgentServerClient.
/// Sends gzip-compressed JSON to the monitoring server.
/// All fact batch payloads are compressed — fact IDs share long common
/// prefixes that compress 80–90%.
/// </summary>
public sealed class HttpAgentServerClient : IAgentServerClient, IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public HttpAgentServerClient(string serverUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
        };
    }

    /// <summary>Exposed for the updater, which needs to download from the same host.</summary>
    public HttpClient HttpClient => _http;

    public async Task<AgentRegistrationResponse> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("api/v1/agent/register", request, JsonOpts, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentRegistrationResponse>(JsonOpts, ct)
         ?? throw new InvalidOperationException("Empty registration response from server.");
    }

    public async Task<bool> CheckApprovalAsync(
        string agentId,
        string apiKey,
        HeartbeatRequest request,
        CancellationToken ct
    )
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "api/v1/agent/heartbeat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(request, options: JsonOpts);

        HttpResponseMessage response = await _http.SendAsync(req, ct);

        // 200 = approved and heartbeat accepted
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        // 403 not_approved = still pending
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return false;
        }

        // Anything else is a real error
        response.EnsureSuccessStatusCode();
        return false; // unreachable
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(
        string agentId,
        string apiKey,
        HeartbeatRequest request,
        CancellationToken ct
    )
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "api/v1/agent/heartbeat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(request, options: JsonOpts);

        HttpResponseMessage response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOpts, ct)
         ?? throw new InvalidOperationException("Empty heartbeat response from server.");
    }

    public async Task<AgentFactsResponse> PostFactsAsync(
        string apiKey,
        AgentFactsRequest request,
        CancellationToken ct
    )
    {
        // Gzip-compress the payload — fact IDs are highly compressible
        using MemoryStream body = new(64 * 1024);
        await using (GZipStream gz = new(body, CompressionLevel.Optimal, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gz, request, JsonOpts, ct);
        }

        body.Seek(0, SeekOrigin.Begin);

        using HttpRequestMessage req = new(HttpMethod.Post, "api/v1/agent/facts");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StreamContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Content.Headers.ContentEncoding.Add("gzip");

        HttpResponseMessage response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AgentFactsResponse>(JsonOpts, ct)
         ?? throw new InvalidOperationException("Empty facts response from server.");
    }

    public void Dispose() => _http.Dispose();
}