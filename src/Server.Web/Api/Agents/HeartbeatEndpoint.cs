using System.Security.Claims;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Agents;

public static class HeartbeatEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/heartbeat", Heartbeat);
    }

    private static async Task<IResult> Heartbeat(
        HeartbeatRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AgentConfigAssembler assembler,
        ReleaseManager releases,
        CancellationToken ct
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

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        JsonElement? capabilities = request.Collectors is null && request.Scanners is null
            ? null
            : JsonSerializer.SerializeToElement(
                new
                {
                    collectors = request.Collectors ?? [],
                    scanners = request.Scanners ?? [],
                },
                JsonOpts
            );

        HeartbeatStatusResult heartbeatResult = await conn.UpdateAgentHeartbeatAsync(
                agentId,
                request.Version,
                request.PassiveDiscoveryMode,
                capabilities,
                ct
            )
            .FirstOrDefaultAsync(ct);

        // UpdateAgentHeartbeatAsync returns no rows when agent not found, so default struct = not found
        // We detect "no rows" by checking if the tuple is the default (Status == null and row wasn't found)
        // Since RETURNING status always returns the row when found, null Status means no row matched.
        if (heartbeatResult == default)
        {
            return ApiError.NotFound("Agent not found.");
        }

        if (heartbeatResult.Status != "approved")
        {
            return ApiError.Problem(403, "not_approved", "Agent is not approved.");
        }

        // Assemble and return the agent's server-side config block. The agent applies
        // intervals, collector enable/disable, and remote targets from this block.
        HeartbeatConfig? config = await assembler.AssembleAsync(agentId, ct);

        UpdateOffer? update = BuildUpdateOffer(context, releases, heartbeatResult.Os, heartbeatResult.Arch, request.Version);

        return Results.Ok(
            new
            {
                status = "approved",
                config,
                update,
            }
        );
    }

    // Offers an update only when a strictly-newer, signed release exists on disk for
    // the agent's platform. An unsigned entry (no .sig sidecar) is never offered —
    // the agent's Updater rejects an offer with an empty signature anyway, so
    // offering it would just fail every heartbeat.
    private static UpdateOffer? BuildUpdateOffer(
        HttpContext context,
        ReleaseManager releases,
        string? os,
        string? arch,
        string? agentVersion
    )
    {
        if (!releases.Enabled || string.IsNullOrEmpty(os) || string.IsNullOrEmpty(arch))
        {
            return null;
        }

        ReleaseEntry? entry = releases.Latest(os, arch);
        if (entry is null || string.IsNullOrEmpty(entry.Signature))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(agentVersion) && !ReleaseManager.SemverGreater(agentVersion, entry.Version))
        {
            return null;
        }

        // Absolute URL on the same scheme+host the agent used to reach us — Updater.ValidateOffer
        // rejects any host that doesn't match the agent's configured server URL.
        string url =
            $"{context.Request.Scheme}://{context.Request.Host}/api/v1/agent/releases/{entry.Version}/{entry.Filename}";

        return new UpdateOffer(
            Version: entry.Version,
            Url: url,
            Sha256: entry.Sha256,
            Size: entry.Size,
            Signature: entry.Signature,
            SignatureAlgorithm: AgentUpdateSigning.Algorithm
        );
    }
}

public sealed record HeartbeatRequest(
    Guid AgentId,
    string? Version,
    string? PassiveDiscoveryMode,
    IReadOnlyList<AgentCapability>? Collectors = null,
    IReadOnlyList<AgentCapability>? Scanners = null
);

/// <summary>Server-side shape of one reported collector/scanner capability — must serialize
/// (snake_case) to match the Agent's own <c>AgentCapability</c> record field-for-field.</summary>
public sealed record AgentCapability(string Name, bool Supported);

/// <summary>Server-side shape of the update offer carried in the heartbeat response — must
/// serialize (snake_case) to match Agent's <c>UpdateInfo</c> record field-for-field.</summary>
public sealed record UpdateOffer(
    string Version,
    string Url,
    string Sha256,
    long Size,
    string Signature,
    string SignatureAlgorithm
);