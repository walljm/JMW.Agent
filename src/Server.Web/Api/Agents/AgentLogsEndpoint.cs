using System.Security.Claims;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Infrastructure;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Agent-facing endpoint that receives one on-demand page of the agent's own recent
/// console/journald log output and hands it to the in-memory <see cref="AgentLogCache" />.
/// Authenticated by <see cref="AgentApiKeyMiddleware" /> like every other /api/v1/agent/*
/// endpoint. The uploaded text is NEVER written to the database — it lives only in the cache,
/// evicted after a short TTL (docs/plans/agent-log-viewer.md §2/§4.3).
/// </summary>
public static class AgentLogsEndpoint
{
    /// <summary>Backstop cap on stored text (chars), in case a buggy/hostile agent uploads far
    /// more than its own byte ceiling should allow. The agent already caps each page at ~128 KB.</summary>
    private const int MaxTextChars = 512 * 1024;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/logs", Upload);
    }

    private static IResult Upload(
        AgentLogUploadRequest request,
        HttpContext context,
        AgentLogCache cache
    )
    {
        string? agentIdClaim = context.User.FindFirstValue("agent_id");
        if (string.IsNullOrEmpty(agentIdClaim) || !Guid.TryParse(agentIdClaim, out Guid agentId))
        {
            return ApiError.Problem(401, "unauthorized", "Invalid API key.");
        }

        if (request.AgentId != agentId)
        {
            return ApiError.Problem(403, "agent_id_mismatch", "agent_id does not match authenticated agent.");
        }

        string text = request.Text;
        bool truncated = request.Truncated;
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
            truncated = true;
        }

        // Normalize the source label to the two known values so the UI can rely on it; anything
        // else (shouldn't happen from our own agent) falls back to the buffer label.
        string source = request.Source == "journald" ? "journald" : "buffer";

        cache.Set(
            agentId,
            new AgentLogBundle(
                RequestedAt: request.RequestedAt,
                ReceivedAt: DateTimeOffset.UtcNow,
                Source: source,
                Truncated: truncated,
                Text: text,
                NextBeforeToken: request.NextBeforeToken
            )
        );

        return Results.Ok(new { status = "received" });
    }
}

/// <summary>Body the agent POSTs to /api/v1/agent/logs. Field-for-field (snake_case) counterpart
/// of the agent's own upload record. <c>RequestedAt</c> echoes the <c>logs_requested_at</c> the
/// server delivered in the heartbeat config, so the UI can match a received page to the request
/// it issued. <c>NextBeforeToken</c> is the paging token for the next older page, or null when the
/// source has nothing older.</summary>
public sealed record AgentLogUploadRequest(
    Guid AgentId,
    DateTimeOffset RequestedAt,
    string Source,
    bool Truncated,
    string Text,
    string? NextBeforeToken
);