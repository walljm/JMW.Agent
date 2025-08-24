using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder AddAgentEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/v1/agent");

        // Unauthenticated endpoints for agent registration and status checking
        group.MapPost("register", RegisterAgent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("authorization-status/{agentId:guid}", GetAuthorizationStatus)
            .Produces<AuthorizationStatusResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return builder;
    }

    private static async Task<IResult> RegisterAgent(
        ApplicationDbContext dbContext,
        [FromBody] AgentRegistrationRequest request,
        ILogger<Program> logger)
    {
        try
        {
            if (request.AgentId == Guid.Empty)
            {
                return Results.BadRequest("Invalid agent ID.");
            }

            if (string.IsNullOrWhiteSpace(request.ServiceName))
            {
                return Results.BadRequest("Service name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.OperatingSystem))
            {
                return Results.BadRequest("Operating system is required.");
            }

            var existingAgent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == request.AgentId);

            if (existingAgent != null)
            {
                // Update existing agent metadata but keep authorization status
                existingAgent.ServiceName = request.ServiceName;
                existingAgent.OperatingSystem = request.OperatingSystem;
                existingAgent.LastSeenAt = DateTime.UtcNow;

                logger.LogInformation("Updated existing agent {AgentId} registration", request.AgentId);
            }
            else
            {
                // Register new agent (unauthorized by default)
                var newAgent = new RegisteredAgent
                {
                    AgentId = request.AgentId,
                    ServiceName = request.ServiceName,
                    OperatingSystem = request.OperatingSystem,
                    IsAuthorized = false,
                    RegisteredAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow
                };

                await dbContext.RegisteredAgents.AddAsync(newAgent);
                logger.LogInformation("Registered new agent {AgentId} with service name {ServiceName}",
                    request.AgentId, request.ServiceName);
            }

            await dbContext.SaveChangesAsync();
            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering agent {AgentId}", request.AgentId);
            return Results.Problem("An error occurred while registering the agent.");
        }
    }

    private static async Task<IResult> GetAuthorizationStatus(
        ApplicationDbContext dbContext,
        Guid agentId,
        ILogger<Program> logger)
    {
        try
        {
            var agent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == agentId);

            if (agent == null)
            {
                return Results.NotFound(new AuthorizationStatusResponse
                {
                    IsAuthorized = false,
                    Message = "Agent not found. Please register first."
                });
            }

            // Update last seen timestamp
            agent.LastSeenAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            var response = new AuthorizationStatusResponse
            {
                IsAuthorized = agent.IsAuthorized,
                Message = agent.IsAuthorized
                    ? "Agent is authorized"
                    : "Agent is pending authorization from administrator"
            };

            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking authorization status for agent {AgentId}", agentId);
            return Results.Problem("An error occurred while checking authorization status.");
        }
    }

    public sealed class AgentRegistrationRequest
    {
        public Guid AgentId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
    }

    public sealed class AuthorizationStatusResponse
    {
        public bool IsAuthorized { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
