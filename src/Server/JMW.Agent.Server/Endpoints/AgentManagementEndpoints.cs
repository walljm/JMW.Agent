using JMW.Agent.Server.Data;
using JMW.Agent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Endpoints;

public static class AgentManagementEndpoints
{
    public static IEndpointRouteBuilder AddAgentManagementEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/v1/admin/agents");

        // These endpoints require authentication (for admin users)
        group.MapGet("", GetAllRegisteredAgents)
            .Produces<IEnumerable<AgentRegistrationSummary>>()
            .RequireAuthorization();

        group.MapPost("{agentId:guid}/authorize", AuthorizeAgent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        group.MapPost("{agentId:guid}/deauthorize", DeauthorizeAgent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        group.MapDelete("{agentId:guid}", DeleteAgent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        return builder;
    }

    private static async Task<IResult> GetAllRegisteredAgents(
        ApplicationDbContext dbContext,
        ILogger<Program> logger)
    {
        try
        {
            var agents = await dbContext.RegisteredAgents
                .OrderByDescending(a => a.RegisteredAt)
                .Select(a => new AgentRegistrationSummary
                {
                    AgentId = a.AgentId,
                    ServiceName = a.ServiceName,
                    OperatingSystem = a.OperatingSystem,
                    IsAuthorized = a.IsAuthorized,
                    RegisteredAt = a.RegisteredAt,
                    LastSeenAt = a.LastSeenAt,
                    AuthorizedAt = a.AuthorizedAt,
                    AuthorizedBy = a.AuthorizedBy
                })
                .ToListAsync();

            return Results.Ok(agents);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving registered agents");
            return Results.Problem("An error occurred while retrieving agents.");
        }
    }

    private static async Task<IResult> AuthorizeAgent(
        ApplicationDbContext dbContext,
        Guid agentId,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var agent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == agentId);

            if (agent == null)
            {
                return Results.NotFound($"Agent {agentId} not found.");
            }

            if (!agent.IsAuthorized)
            {
                agent.IsAuthorized = true;
                agent.AuthorizedAt = DateTime.UtcNow;
                // You could get the current user's name from the auth context if needed
                agent.AuthorizedBy = context.User?.Identity?.Name ?? "Admin";

                await dbContext.SaveChangesAsync();

                logger.LogInformation("Agent {AgentId} ({ServiceName}) has been authorized by {AuthorizedBy}",
                    agentId, agent.ServiceName, agent.AuthorizedBy);
            }

            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error authorizing agent {AgentId}", agentId);
            return Results.Problem("An error occurred while authorizing the agent.");
        }
    }

    private static async Task<IResult> DeauthorizeAgent(
        ApplicationDbContext dbContext,
        Guid agentId,
        HttpContext context,
        ILogger<Program> logger)
    {
        try
        {
            var agent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == agentId);

            if (agent == null)
            {
                return Results.NotFound($"Agent {agentId} not found.");
            }

            if (agent.IsAuthorized)
            {
                agent.IsAuthorized = false;
                agent.AuthorizedAt = null;
                agent.AuthorizedBy = null;

                await dbContext.SaveChangesAsync();

                logger.LogInformation("Agent {AgentId} ({ServiceName}) has been deauthorized",
                    agentId, agent.ServiceName);
            }

            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deauthorizing agent {AgentId}", agentId);
            return Results.Problem("An error occurred while deauthorizing the agent.");
        }
    }

    private static async Task<IResult> DeleteAgent(
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
                return Results.NotFound($"Agent {agentId} not found.");
            }

            dbContext.RegisteredAgents.Remove(agent);
            await dbContext.SaveChangesAsync();

            logger.LogInformation("Agent {AgentId} ({ServiceName}) has been deleted",
                agentId, agent.ServiceName);

            return Results.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting agent {AgentId}", agentId);
            return Results.Problem("An error occurred while deleting the agent.");
        }
    }

    public sealed class RegisteredAgentResponse
    {
        public Guid AgentId { get; set; }
        public string ServiceName { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public bool IsAuthorized { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public DateTime? AuthorizedAt { get; set; }
        public string? AuthorizedBy { get; set; }
    }
}
