using JMW.Agent.Common.Models;

namespace JMW.Agent.Client;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder AddAgentEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("api/v1/info", Get)
           .Produces<JmwMachineInformation>()
           ;

        return builder;
    }

    private static IResult Get()
    {
        var info = JmwMachineInformation.GetInfo();
        return Results.Ok(info);
    }
}
