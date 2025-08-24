using System.Net;
using System.Text.Json;
using JMW.Agent.Client.Services;
using JMW.Agent.Common.Models;
using JMW.Agent.Common.Serialization;
using Microsoft.Extensions.Options;

namespace JMW.Agent.Client;

public sealed class ReportingService : BackgroundService
{
    private readonly ILogger<ReportingService> logger;
    private readonly IHostApplicationLifetime hostApplication;
    private readonly IHttpClientFactory clientFactory;
    private readonly IOptions<AgentOptions> options;
    private readonly IAgentIdentifierService agentIdentifierService;
    private readonly IAgentRegistrationService agentRegistrationService;

    public ReportingService(
        ILogger<ReportingService> logger,
        IHostApplicationLifetime hostApplication,
        IHttpClientFactory clientFactory,
        IOptions<AgentOptions> options,
        IAgentIdentifierService agentIdentifierService,
        IAgentRegistrationService agentRegistrationService
    )
    {
        this.logger = logger;
        this.hostApplication = hostApplication;
        this.clientFactory = clientFactory;
        this.options = options;
        this.agentIdentifierService = agentIdentifierService;
        this.agentRegistrationService = agentRegistrationService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            #region Validations

            if (options.Value is null)
            {
                throw new InvalidDataException("AgentOptions must be configured.");
            }

            if (string.IsNullOrEmpty(options.Value.ServerIp) || !IPAddress.TryParse(options.Value.ServerIp, out var ip))
            {
                throw new InvalidDataException("Server IP must be populated.");
            }

            if (options.Value.ServiceName is null)
            {
                throw new InvalidDataException("ServiceName must be populated.");
            }

            #endregion Validations

            // Get or create agent identifier
            var agentId = await agentIdentifierService.GetOrCreateAgentIdAsync();
            var operatingSystem = await agentIdentifierService.GetOperatingSystemInfoAsync();

            // Register agent with server
            logger.LogInformation("Registering agent {AgentId} with server...", agentId);
            var registrationSuccess = await agentRegistrationService.RegisterAgentAsync(agentId, options.Value.ServiceName, operatingSystem);

            if (!registrationSuccess)
            {
                logger.LogWarning("Failed to register agent, will retry...");
                // Continue anyway - registration will be retried during authorization check
            }

            // Wait for authorization before starting data reporting
            logger.LogInformation("Waiting for agent authorization...");
            await agentRegistrationService.WaitForAuthorizationAsync(agentId, stoppingToken);

            logger.LogInformation("Agent authorized! Starting data reporting...");

            var httpClient = GetHttpClient(agentId, $"https://{options.Value.ServerIp}:{options.Value.ServerPort}");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Verify we're still authorized before posting data
                    var stillAuthorized = await agentRegistrationService.CheckAuthorizationStatusAsync(agentId);
                    if (!stillAuthorized)
                    {
                        logger.LogWarning("Agent is no longer authorized. Waiting for re-authorization...");
                        await agentRegistrationService.WaitForAuthorizationAsync(agentId, stoppingToken);
                        continue;
                    }

                    var info = JmwMachineInformation.GetInfo();
                    var service = new AgentDataPayload
                    {
                        // AgentId and ServiceName will be set by the server endpoint using agent context
                        InfoJson = JsonSerializer.Serialize(info, SystemTextJsonSerializerSettingsProvider.Default),
                    };

                    var registration = await httpClient.PostAsJsonAsync("/api/v1/agent/data", service, cancellationToken: stoppingToken);
                    if (!registration.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Failed to post agent data. Status: {StatusCode}", registration.StatusCode);
                    }
                    else
                    {
                        logger.LogDebug("Successfully posted agent data");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred trying to post agent update.");
                }

                await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ReportingService stopped");
        }
        catch (Exception e)
        {
            Environment.ExitCode = -1;

            logger.LogError(e, "Failed to start.");
            hostApplication.StopApplication();

            return;
        }
    }

    private HttpClient GetHttpClient(Guid agentId, string baseAddress)
    {
        var httpClient = clientFactory.CreateClient(nameof(ReportingService));

        // Use agent ID as authorization header
        httpClient.DefaultRequestHeaders.Add("X-Agent-Id", agentId.ToString());

        httpClient.BaseAddress = new Uri(baseAddress);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JMW.Agent");

        return httpClient;
    }
}