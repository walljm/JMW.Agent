using System.Security.Claims;

using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Authenticates agent requests on /api/v1/agent/* (except /api/v1/agent/register).
/// Reads Authorization: Bearer key and looks up the agent by key hash. Each endpoint is
/// responsible for verifying its own body's agent_id against the "agent_id" claim set here
/// (see FactsEndpoint/HeartbeatEndpoint) — a body-agnostic check here can't work for every
/// endpoint (the /facts body is gzip-compressed) so it isn't attempted at this layer.
/// </summary>
public sealed class AgentApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public AgentApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private const string AgentPathPrefix = "/api/v1/agent/";
    private const string RegisterPath = "/api/v1/agent/register";

    public async Task InvokeAsync(HttpContext context, NpgsqlDataSource db, ApiKeyService apiKeys)
    {
        string path = context.Request.Path.Value ?? string.Empty;

        if (!path.StartsWith(AgentPathPrefix, StringComparison.OrdinalIgnoreCase)
         || path.Equals(RegisterPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        string authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context, "Invalid API key.");
            return;
        }

        string keyPlaintext = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(keyPlaintext))
        {
            await WriteUnauthorizedAsync(context, "Invalid API key.");
            return;
        }

        string keyHash = apiKeys.Hash(keyPlaintext);

        // Look up agent by hash
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(context.RequestAborted);
        (Guid AgentId, string Status) agentRow = await conn.FindAgentByHashAsync(keyHash, context.RequestAborted)
            .FirstOrDefaultAsync(context.RequestAborted);

        if (agentRow == default)
        {
            await WriteUnauthorizedAsync(context, "Invalid API key.");
            return;
        }

        Claim[] claims = new[]
        {
            new Claim("agent_id", agentRow.AgentId.ToString()),
            new Claim(ClaimTypes.Role, "agent"),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "agent-api-key"));

        await _next(context);
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":{{\"code\":\"unauthorized\",\"message\":\"{message}\"}}}}"
        );
    }
}