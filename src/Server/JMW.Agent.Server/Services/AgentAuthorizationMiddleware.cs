using JMW.Agent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace JMW.Agent.Server.Services;

public class AgentAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentAuthorizationMiddleware> _logger;

    public AgentAuthorizationMiddleware(RequestDelegate next, ILogger<AgentAuthorizationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IServiceProvider serviceProvider)
    {
        // Only check agent authorization for agent data submission endpoints
        if (context.Request.Path.StartsWithSegments("/api/v1/agent/data"))
        {
            var agentIdHeader = context.Request.Headers["X-Agent-Id"].FirstOrDefault();

            if (string.IsNullOrEmpty(agentIdHeader) || !Guid.TryParse(agentIdHeader, out var agentId))
            {
                _logger.LogWarning("Missing or invalid X-Agent-Id header for path: {Path}", context.Request.Path);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Missing or invalid agent identifier");
                return;
            }

            // Create scope to get database context
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var agent = await dbContext.RegisteredAgents
                .FirstOrDefaultAsync(a => a.AgentId == agentId);

            if (agent == null)
            {
                _logger.LogWarning("Agent {AgentId} not found", agentId);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Agent not registered");
                return;
            }

            if (!agent.IsAuthorized)
            {
                _logger.LogWarning("Agent {AgentId} is not authorized", agentId);
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Agent not authorized");
                return;
            }

            // Update last seen timestamp for authorized agents
            agent.LastSeenAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();

            // Add agent information to context for use in endpoints
            context.Items["AgentId"] = agentId;
            context.Items["Agent"] = agent;
        }

        await _next(context);
    }
}
