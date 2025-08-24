using JMW.Agent.Client.Services;
using JMW.Agent.Common.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace JMW.Agent.Client;

public sealed class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        /*
            ZipArchive doesn't fully support asynchronous operations.
            https://github.com/dotnet/runtime/issues/1560
            https://github.com/dotnet/runtime/issues/1541
        */
        services.Configure<KestrelServerOptions>(options =>
        {
            options.AllowSynchronousIO = true;
        });

        services.Configure<JsonOptions>(static options =>
        {
            SystemTextJsonSerializerSettingsProvider.Apply(options.SerializerOptions);
        });

        services.AddHttpClient(nameof(ReportingService))
            .ConfigurePrimaryHttpMessageHandler(
                static svc => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
                    ClientCertificateOptions = ClientCertificateOption.Manual,
                }
            );

        // Add HttpClient for agent registration service
        services.AddHttpClient<IAgentRegistrationService, AgentRegistrationService>()
            .ConfigurePrimaryHttpMessageHandler(
                static svc => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
                    ClientCertificateOptions = ClientCertificateOption.Manual,
                }
            );

        services.AddOptions<AgentOptions>()
            .Bind(this.Configuration.GetSection(nameof(AgentOptions)))
            .ValidateDataAnnotations();

        // Register new agent services
        services.AddSingleton<IAgentIdentifierService, AgentIdentifierService>();
        services.AddSingleton<IAgentRegistrationService, AgentRegistrationService>();

        services.AddHostedService<ReportingService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseHttpsRedirection();

        app.UseRouting();

        app.UseEndpoints(static endpoints =>
        {
            // No agent endpoints needed - agents now push data to server
        });
    }
}