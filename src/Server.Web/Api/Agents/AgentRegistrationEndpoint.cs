using System.Text.RegularExpressions;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Agents;

public static class AgentRegistrationEndpoint
{
    // RFC 1123 hostname: dot-separated labels of alphanumerics/hyphens, no leading/trailing hyphen per label.
    public static readonly Regex HostnamePattern =
        new(@"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/register", Register).RequireRateLimiting("agent-register");
    }

    private static async Task<IResult> Register(
        AgentRegisterRequest request,
        NpgsqlDataSource db,
        ApiKeyService apiKeys,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (request.AgentId == Guid.Empty)
        {
            return ApiError.Problem(400, "invalid_agent_id", "agent_id is required.");
        }

        if (!string.IsNullOrEmpty(request.Hostname) && request.Hostname.Length > 253)
        {
            return ApiError.Problem(400, "invalid_hostname", "hostname exceeds maximum length.");
        }

        if (!string.IsNullOrEmpty(request.Hostname) && !HostnamePattern.IsMatch(request.Hostname))
        {
            return ApiError.Problem(400, "invalid_hostname", "hostname contains invalid characters.");
        }

        if (!string.IsNullOrEmpty(request.Zone) && request.Zone.Length > 64)
        {
            return ApiError.Problem(400, "invalid_zone", "zone exceeds maximum length.");
        }

        if (!string.IsNullOrEmpty(request.Version) && request.Version.Length > 64)
        {
            return ApiError.Problem(400, "invalid_version", "version exceeds maximum length.");
        }

        if (!string.IsNullOrEmpty(request.IpAddress) && request.IpAddress.Length > 45)
        {
            return ApiError.Problem(400, "invalid_ip", "ip_address exceeds maximum length.");
        }

        if (!string.IsNullOrEmpty(request.Os) && request.Os.Length > 64)
        {
            return ApiError.Problem(400, "invalid_os", "os exceeds maximum length.");
        }

        if (!string.IsNullOrEmpty(request.Arch) && request.Arch.Length > 32)
        {
            return ApiError.Problem(400, "invalid_arch", "arch exceeds maximum length.");
        }

        string plaintext = apiKeys.Generate();
        string keyHash = apiKeys.Hash(plaintext);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        try
        {
            await conn.InsertAgentAsync(
                    request.AgentId,
                    request.Hostname,
                    keyHash,
                    request.Zone,
                    request.Version,
                    request.PassiveDiscoveryMode,
                    request.Os,
                    request.Arch,
                    request.IpAddress,
                    ct
                )
                .ExecuteAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique constraint violation — agent_id already exists
            return ApiError.Problem(409, "agent_exists", "An agent with this agent_id already exists.");
        }

        await audit.WriteAsync(
            $"agent:{request.AgentId}",
            "agent.register",
            request.AgentId.ToString(),
            ct: ct
        );

        return Results.Json(
            new
            {
                agent_id = request.AgentId,
                status = "pending",
                api_key = plaintext,
            },
            statusCode: 201
        );
    }
}

public sealed record AgentRegisterRequest(
    Guid AgentId,
    string Hostname,
    string Version,
    string? Zone,
    string? PassiveDiscoveryMode,
    string? Os,
    string? Arch,
    string? IpAddress
);