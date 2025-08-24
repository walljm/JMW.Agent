using System.Text;
using System.Text.Json;
using JMW.Agent.Common.Serialization;
using Microsoft.Extensions.Options;

namespace JMW.Agent.Client.Services;

public interface IAgentRegistrationService
{
    Task<bool> RegisterAgentAsync(Guid agentId, string serviceName, string operatingSystem);
    Task<bool> CheckAuthorizationStatusAsync(Guid agentId);
    Task WaitForAuthorizationAsync(Guid agentId, CancellationToken cancellationToken = default);
}

public sealed class AgentRegistrationService : IAgentRegistrationService
{
    private readonly HttpClient _httpClient;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentRegistrationService> _logger;

    public AgentRegistrationService(HttpClient httpClient, IOptions<AgentOptions> options, ILogger<AgentRegistrationService> logger)
    {
        _httpClient = httpClient;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options), "AgentOptions configuration is missing");
        _logger = logger;
    }

    public async Task<bool> RegisterAgentAsync(Guid agentId, string serviceName, string operatingSystem)
    {
        try
        {
            var registrationData = new
            {
                AgentId = agentId,
                ServiceName = serviceName,
                OperatingSystem = operatingSystem
            };

            var json = JsonSerializer.Serialize(registrationData, SystemTextJsonSerializerSettingsProvider.Default);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var baseUrl = $"https://{_options.ServerIp}:{_options.ServerPort}";
            var response = await _httpClient.PostAsync($"{baseUrl}/api/v1/agent/register", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully registered agent {AgentId} with service name {ServiceName}", agentId, serviceName);
                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to register agent. Status: {StatusCode}, Content: {Content}", response.StatusCode, errorContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering agent {AgentId}", agentId);
            return false;
        }
    }

    public async Task<bool> CheckAuthorizationStatusAsync(Guid agentId)
    {
        try
        {
            var baseUrl = $"https://{_options.ServerIp}:{_options.ServerPort}";
            var response = await _httpClient.GetAsync($"{baseUrl}/api/v1/agent/authorization-status/{agentId}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var statusResponse = JsonSerializer.Deserialize<AuthorizationStatusResponse>(content, SystemTextJsonSerializerSettingsProvider.Default);

                if (statusResponse?.IsAuthorized == true)
                {
                    _logger.LogInformation("Agent {AgentId} is authorized", agentId);
                    return true;
                }

                _logger.LogDebug("Agent {AgentId} is not yet authorized: {Message}", agentId, statusResponse?.Message);
                return false;
            }

            _logger.LogWarning("Failed to check authorization status. Status: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking authorization status for agent {AgentId}", agentId);
            return false;
        }
    }

    public async Task WaitForAuthorizationAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var pollInterval = TimeSpan.FromSeconds(30);
        var maxPollInterval = TimeSpan.FromMinutes(5);
        var backoffMultiplier = 1.5;
        var consecutiveFailures = 0;

        _logger.LogInformation("Waiting for agent {AgentId} to be authorized...", agentId);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var isAuthorized = await CheckAuthorizationStatusAsync(agentId);
                if (isAuthorized)
                {
                    _logger.LogInformation("Agent {AgentId} has been authorized!", agentId);
                    return;
                }

                // Reset failure count on successful check
                consecutiveFailures = 0;
                pollInterval = TimeSpan.FromSeconds(30);
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.LogWarning(ex, "Failed to check authorization status (attempt {Attempt})", consecutiveFailures);

                // Apply exponential backoff on failures
                pollInterval = TimeSpan.FromMilliseconds(Math.Min(
                    pollInterval.TotalMilliseconds * backoffMultiplier,
                    maxPollInterval.TotalMilliseconds));
            }

            _logger.LogDebug("Waiting {PollInterval} before next authorization check", pollInterval);
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private sealed class AuthorizationStatusResponse
    {
        public bool IsAuthorized { get; set; }
        public string? Message { get; set; }
    }
}
