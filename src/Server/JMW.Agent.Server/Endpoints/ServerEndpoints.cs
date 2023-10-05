using JMW.Agent.Common.Models;
using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace JMW.Agent;

public static class ServerEndpoints
{
    public static IEndpointRouteBuilder AddServerEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/v1/server");

        group.MapGet("agents", GetAll)
           .Produces<IEnumerable<AgentInformation>>()
           ;

        group.MapPost("agent", Post)
            .Produces(StatusCodes.Status200OK)
            ;

        return builder;
    }

    private static async Task<IResult> Post(ApplicationDbContext dbContext, [FromBody] AgentService service)
    {
        if (service.Name is null)
        {
            return Results.BadRequest("Service has no name.");
        }

        var dbClient = await dbContext.FindAsync<AgentService>(service.Name);
        if (dbClient is null)
        {
            await dbContext.AddAsync(service);
        }
        else
        {
            dbClient.InfoJson = service.InfoJson;
        }

        await dbContext.SaveChangesAsync();

        return Results.Ok();
    }

    private static IResult GetAll(ApplicationDbContext dbContext)
    {
        var clients = dbContext.AgentServices
                               .Select(o => new AgentInformation
                               {
                                   MachineInformation = o.InfoJson != null
                                    ? JsonSerializer.Deserialize<JmwMachineInformation>(o.InfoJson, SystemTextJsonSerializerSettingsProvider.Default)
                                    : null,
                                   ServiceName = o.Name,
                               });

        return Results.Ok(clients);
    }
}
