using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace JMW.Agent.Server;

public static class MetaEndpointExtensions
{
    public static void MapMetaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/meta");
        group.MapPrometheusScrapingEndpoint("/metrics");
        group.MapHealthChecks("/health");
    }

    public static void AddMetricsCollector(this IServiceCollection services)
    {
        const string name = $"{nameof(JMW)}.{nameof(JMW.Agent)}.{nameof(JMW.Agent.Server)}";

        services.AddOpenTelemetry()
            .WithMetrics(opts =>
            {
                opts.AddAspNetCoreInstrumentation();
                opts.AddRuntimeInstrumentation();
                opts.AddProcessInstrumentation();
                opts.AddPrometheusExporter();
            })
            .WithTracing(tracerBuilderProvider =>
            {
                tracerBuilderProvider.AddSource(name);
                tracerBuilderProvider.ConfigureResource(resource => resource.AddService(name));
                tracerBuilderProvider.AddAspNetCoreInstrumentation();
                //tracerBuilderProvider.AddConsoleExporter();
            })
            ;
    }
}