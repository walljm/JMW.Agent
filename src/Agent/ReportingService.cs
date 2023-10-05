namespace JMW.Agent.Client;

using JMW.Agent.Common.Models;
using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

            if (this.options?.Value is null)
            {
                throw new InvalidDataException("AgentOptions must be configured.");
            }

            if (string.IsNullOrEmpty(this.options?.Value?.ServerIp) || !IPAddress.TryParse(this.options?.Value?.ServerIp, out var ip))
            {
                throw new InvalidDataException("Server IP must be populated.");
            }
            if (string.IsNullOrEmpty(this.options?.Value?.Token))
            {
                throw new InvalidDataException("Token must be populated.");
            }

            if (this.options?.Value?.ServiceName is null)
            {
                throw new InvalidDataException("Registration must be populated.");
            }

            #endregion Validations

            var httpClient = this.GetHttpClient(this.options.Value.Token, $"https://{this.options.Value.ServerIp}:{this.options.Value.ServerPort}");

            while (true)
            {
                try
                {
                    var info = JmwMachineInformation.GetInfo();
                    var service = new AgentService
                    {
                        Name = this.options.Value.ServiceName,
                        InfoJson = JsonSerializer.Serialize(info, SystemTextJsonSerializerSettingsProvider.Default),
                    };
                    var registration = await httpClient.PostAsJsonAsync("/api/v1/server/agent", service, cancellationToken: stoppingToken);

                    if (!registration.IsSuccessStatusCode)
                    {
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Error occurred trying to post agent update.");
                }

                await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
            }
        }
        catch (Exception e)
        {
            Environment.ExitCode = -1;

            this.logger.LogError(e, "Failed to start.");
            this.hostApplication.StopApplication();

            return;
        }
    }

    private HttpClient GetHttpClient(string token, string baseAddress)
    {
        var httpClient = this.clientFactory.CreateClient(nameof(ReportingService));

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            scheme: "Basic",
            parameter: Convert.ToBase64String(Encoding.UTF8.GetBytes($"{token}")));

        httpClient.BaseAddress = new Uri(baseAddress);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JMW.Agent");

        return httpClient;
    }
}
