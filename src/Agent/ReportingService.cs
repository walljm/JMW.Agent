using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JMW.Agent.Common.Models;
using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Models;
using Microsoft.Extensions.Options;

namespace JMW.Agent.Client;

public sealed class ReportingService : BackgroundService
{
    private readonly ILogger<ReportingService> logger;
    private readonly IHostApplicationLifetime hostApplication;
    private readonly IHttpClientFactory clientFactory;
    private readonly IOptions<AgentOptions> options;

    public ReportingService(
        ILogger<ReportingService> logger,
        IHostApplicationLifetime hostApplication,
        IHttpClientFactory clientFactory,
        IOptions<AgentOptions> options
    )
    {
        this.logger = logger;
        this.hostApplication = hostApplication;
        this.clientFactory = clientFactory;
        this.options = options;
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

            if (string.IsNullOrEmpty(options.Value.Token))
            {
                throw new InvalidDataException("Token must be populated.");
            }

            if (options.Value.ServiceName is null)
            {
                throw new InvalidDataException("Registration must be populated.");
            }

            #endregion Validations

            var httpClient = GetHttpClient(options.Value.Token, $"https://{options.Value.ServerIp}:{options.Value.ServerPort}");

            while (true)
            {
                try
                {
                    var info = JmwMachineInformation.GetInfo();
                    var service = new AgentService
                    {
                        Name = options.Value.ServiceName,
                        InfoJson = JsonSerializer.Serialize(info, SystemTextJsonSerializerSettingsProvider.Default),
                    };
                    var registration = await httpClient.PostAsJsonAsync("/api/v1/server/agent", service, cancellationToken: stoppingToken);
                    if (!registration.IsSuccessStatusCode)
                    {
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred trying to post agent update.");
                }

                await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
            }
        }
        catch (Exception e)
        {
            Environment.ExitCode = -1;

            logger.LogError(e, "Failed to start.");
            hostApplication.StopApplication();

            return;
        }
    }

    private HttpClient GetHttpClient(string token, string baseAddress)
    {
        var httpClient = clientFactory.CreateClient(nameof(ReportingService));

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            scheme: "Basic",
            parameter: Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token}"))
        );

        httpClient.BaseAddress = new Uri(baseAddress);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JMW.Agent");

        return httpClient;
    }
}