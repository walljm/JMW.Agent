using System.Text.Json;
using JMW.Agent.Common.Models;
using JMW.Agent.Common.Serialization;
using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Endpoints;

public static class AgentDataEndpoints
{
    public static IEndpointRouteBuilder AddAgentDataEndpoints(this IEndpointRouteBuilder builder)
    {
        var agentGroup = builder.MapGroup("/api/v1/agent");
        var adminGroup = builder.MapGroup("/api/v1/admin");

        // Agent data submission endpoint (protected by AgentAuthorizationMiddleware)
        agentGroup.MapPost("data", SubmitAgentData)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        // Admin endpoints for viewing active agents and their data
        adminGroup.MapGet("active-agents", GetActiveAgents)
            .Produces<IEnumerable<ActiveAgentSummary>>()
            .RequireAuthorization();

        adminGroup.MapGet("active-agents/{agentId:guid}/details", GetAgentDetails)
            .Produces<AgentDetailResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        return builder;
    }

    private static async Task<IResult> SubmitAgentData(
        ApplicationDbContext dbContext,
        [FromBody] AgentDataPayload payload,
        HttpContext context)
    {
        // Get agent information from context (set by middleware)
        var agentId = context.Items["AgentId"] as Guid?;
        var agent = context.Items["Agent"] as RegisteredAgent;

        if (agentId == null || agent == null)
        {
            return Results.BadRequest("Invalid agent context.");
        }

        try
        {
            // Set the agent ID and service name from the registered agent
            payload.AgentId = agentId.Value;
            payload.ServiceName = agent.ServiceName;
            payload.LastUpdated = DateTime.UtcNow;

            var existingPayload = await dbContext.AgentDataPayloads
                .FirstOrDefaultAsync(p => p.AgentId == agentId.Value);

            if (existingPayload == null)
            {
                await dbContext.AgentDataPayloads.AddAsync(payload);
            }
            else
            {
                existingPayload.InfoJson = payload.InfoJson;
                existingPayload.LastUpdated = payload.LastUpdated;
            }

            await dbContext.SaveChangesAsync();
            return Results.Ok();
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error saving agent data: {ex.Message}");
        }
    }

    private static async Task<IResult> GetActiveAgents(ApplicationDbContext dbContext)
    {
        try
        {
            // First, get the basic agent data with payload information
            var agentsWithPayloads = await dbContext.RegisteredAgents
                .Where(static a => a.IsAuthorized)
                .Select(a => new
                {
                    Agent = a,
                    Payload = dbContext.AgentDataPayloads
                       .FirstOrDefault(p => p.AgentId == a.AgentId)
                })
                .ToListAsync();

            // Process the machine information in memory (not in SQL)
            var activeAgents = agentsWithPayloads.Select(static item =>
                {
                    JmwMachineInformation? machineInfo = null;
                    if (item.Payload?.InfoJson != null)
                    {
                        try
                        {
                            machineInfo = JsonSerializer.Deserialize<JmwMachineInformation>(
                                item.Payload.InfoJson,
                                SystemTextJsonSerializerSettingsProvider.Default);
                        }
                        catch
                        {
                            // Ignore deserialization errors
                        }
                    }

                    return new ActiveAgentSummary
                    {
                        AgentId = item.Agent.AgentId,
                        ServiceName = item.Agent.ServiceName,
                        OperatingSystem = item.Agent.OperatingSystem,
                        LastSeenAt = item.Agent.LastSeenAt,
                        LastDataUpdate = item.Payload?.LastUpdated ?? DateTime.MinValue,
                        HasMachineData = item.Payload != null,
                        MachineName = machineInfo?.MachineName
                    };
                })
            .OrderByDescending(static a => a.LastSeenAt)
            .ToList();

            return Results.Ok(activeAgents);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error retrieving active agents: {ex.Message}");
        }
    }

    private static async Task<IResult> GetAgentDetails(ApplicationDbContext dbContext, Guid agentId)
    {
        try
        {
            var agent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == agentId && a.IsAuthorized);

            if (agent == null)
            {
                return Results.NotFound($"Authorized agent {agentId} not found.");
            }

            var agentData = await dbContext.AgentDataPayloads
                .FirstOrDefaultAsync(p => p.AgentId == agentId);

            var machineInfo = agentData?.InfoJson != null
                ? JsonSerializer.Deserialize<JmwMachineInformation>(agentData.InfoJson, SystemTextJsonSerializerSettingsProvider.Default)
                : null;

            var response = new AgentDetailResponse
            {
                AgentId = agent.AgentId,
                ServiceName = agent.ServiceName,
                OperatingSystem = agent.OperatingSystem,
                RegisteredAt = agent.RegisteredAt,
                LastSeenAt = agent.LastSeenAt,
                LastDataUpdate = agentData?.LastUpdated,
                MachineInformation = machineInfo
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error retrieving agent details: {ex.Message}");
        }
    }
}