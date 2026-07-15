using System.Text;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class TargetsApi
{
    private const int DefaultPageLimit = 100;

    // Collector types supported by the agent's device and service collectors. Must stay
    // in sync with the registered collectors (SshCollector, SnmpCollector, …,
    // TechnitiumCollector, HomeAssistantCollector) and the UI dropdown.
    private static readonly HashSet<string> ValidCollectorTypes =
        new(StringComparer.Ordinal)
        {
            "ssh",
            "snmp",
            "http",
            "cert",
            "bacnet",
            "modbus",
            "google-wifi",
            "technitium-dns",
            "home-assistant",
        };

    // Collector types whose endpoint is a full service URL rather than a bare
    // host/IP — these get the absolute http(s) URL validation; everything else just
    // needs a non-blank value.
    private static readonly HashSet<string> UrlStyleCollectorTypes =
        new(StringComparer.Ordinal)
        {
            "technitium-dns",
            "home-assistant",
        };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/targets", ListTargets)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapGet("/targets/candidates", ListTargetCandidates)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/targets", CreateTarget)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/targets/{id}", UpdateTarget)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapDelete("/targets/{id}", DeleteTarget)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/targets/{id}/enabled", ToggleTarget)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> ListTargets(
        NpgsqlDataSource db,
        string? agent_id,
        string? after,
        int limit = DefaultPageLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 200);

        Guid? agentId = null;
        if (!string.IsNullOrEmpty(agent_id))
        {
            if (!Guid.TryParse(agent_id, out Guid parsed))
            {
                return Error("invalid_id", "Invalid agent_id filter.", 400);
            }

            agentId = parsed;
        }

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterTargetId = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!TryDecodeCursor(after, out afterCreatedAt, out afterTargetId))
            {
                return Error("invalid_cursor", "Invalid pagination cursor.", 422);
            }
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(Guid TargetId, Guid AgentId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label,
            bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)> rows = await conn
            .ListTargetsAsync(agentId, afterCreatedAt, afterTargetId, limit + 1, ct)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            (Guid TargetId, Guid AgentId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label,
                bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) last = rows[rows.Count - 1];
            nextCursor = EncodeCursor(last.CreatedAt, last.TargetId);
        }

        List<TargetListItem> items = rows.Select(r => new TargetListItem(
                    TargetId: r.TargetId.ToString(),
                    AgentId: r.AgentId.ToString(),
                    Endpoint: r.Endpoint,
                    CollectorType: r.CollectorType,
                    CredentialId: r.CredentialId?.ToString(),
                    Label: r.Label,
                    Enabled: r.Enabled,
                    CreatedAt: r.CreatedAt.UtcDateTime,
                    UpdatedAt: r.UpdatedAt.UtcDateTime
                )
            )
            .ToList();

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    private static async Task<IResult> ListTargetCandidates(
        NpgsqlDataSource db,
        string agent_id,
        CancellationToken ct = default
    )
    {
        if (!Guid.TryParse(agent_id, out Guid agentId))
        {
            return Error("invalid_id", "Invalid agent_id.", 400);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> rows =
            await conn.ListTargetCandidatesAsync(agentId, ct).ToListAsync(ct);

        // Endpoint/CollectorType are never actually null at runtime (string literals/
        // concatenations in the query's SELECT list) — they're only nullable in the declared
        // shape because Postgres reports UNION ALL result columns as nullable regardless of
        // each branch's own nullability.
        List<TargetCandidate> items = [];
        foreach ((string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model) r in rows)
        {
            if (r is not { Endpoint: { } endpoint, CollectorType: { } collectorType })
            {
                continue;
            }

            items.Add(new TargetCandidate(endpoint, collectorType, r.Hostname, r.Vendor, r.Model));
        }

        return Results.Ok(new { items });
    }

    private static async Task<IResult> CreateTarget(
        CreateTargetRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(request.AgentId, out Guid agentId))
        {
            return Error("invalid_id", "Invalid agent_id.", 400);
        }

        if (!ValidCollectorTypes.Contains(request.CollectorType))
        {
            return Error("invalid_collector_type", $"Unknown collector type '{request.CollectorType}'.", 422);
        }

        if (!TryNormalizeEndpoint(request.CollectorType, request.Endpoint, out string endpoint))
        {
            return Error("invalid_endpoint", EndpointErrorMessage(request.CollectorType), 422);
        }

        Guid? credentialId = null;
        if (!string.IsNullOrEmpty(request.CredentialId))
        {
            if (!Guid.TryParse(request.CredentialId, out Guid cid))
            {
                return Error("invalid_id", "Invalid credential_id.", 400);
            }

            credentialId = cid;
        }

        string? label = NormalizeLabel(request.Label);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        TargetIdResult result = await conn
            .InsertTargetAsync(agentId, endpoint, request.CollectorType, credentialId, label, ct)
            .FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("create_failed", "Failed to create target.", 500);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "target.create", result.TargetId.ToString(), ct: ct);

        return Results.Ok(
            new
            {
                target_id = result.TargetId.ToString(),
                agent_id = request.AgentId,
                endpoint,
                collector_type = request.CollectorType,
                credential_id = credentialId?.ToString(),
                label,
                enabled = true,
            }
        );
    }

    private static async Task<IResult> UpdateTarget(
        string id,
        UpdateTargetRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid targetId))
        {
            return Error("invalid_id", "Invalid target id.", 400);
        }

        if (!ValidCollectorTypes.Contains(request.CollectorType))
        {
            return Error("invalid_collector_type", $"Unknown collector type '{request.CollectorType}'.", 422);
        }

        if (!TryNormalizeEndpoint(request.CollectorType, request.Endpoint, out string endpoint))
        {
            return Error("invalid_endpoint", EndpointErrorMessage(request.CollectorType), 422);
        }

        Guid? credentialId = null;
        if (!string.IsNullOrEmpty(request.CredentialId))
        {
            if (!Guid.TryParse(request.CredentialId, out Guid cid))
            {
                return Error("invalid_id", "Invalid credential_id.", 400);
            }

            credentialId = cid;
        }

        string? label = NormalizeLabel(request.Label);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        TargetIdResult result = await conn
            .UpdateTargetAsync(targetId, endpoint, request.CollectorType, credentialId, label, ct)
            .FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Target not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "target.update", id, ct: ct);

        return Results.Ok(
            new
            {
                target_id = id,
                endpoint,
                collector_type = request.CollectorType,
                credential_id = credentialId?.ToString(),
                label,
            }
        );
    }

    private static async Task<IResult> DeleteTarget(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid targetId))
        {
            return Error("invalid_id", "Invalid target id.", 400);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        TargetIdResult result = await conn.DeleteTargetAsync(targetId, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Target not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "target.delete", id, ct: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ToggleTarget(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid targetId))
        {
            return Error("invalid_id", "Invalid target id.", 400);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (Guid TargetId, bool Enabled) result = await conn.ToggleTargetAsync(targetId, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Target not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "target.toggle", id, ct: ct);

        return Results.Ok(
            new
            {
                target_id = id,
                enabled = result.Enabled,
            }
        );
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the endpoint for the given collector type: an absolute http(s) URL for
    /// URL-style collectors (technitium-dns, home-assistant), or just a non-blank
    /// bare host/IP for everything else.
    /// </summary>
    private static bool TryNormalizeEndpoint(string collectorType, string? raw, out string endpoint)
    {
        if (UrlStyleCollectorTypes.Contains(collectorType))
        {
            return TryNormalizeUrl(raw, out endpoint);
        }

        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        endpoint = raw.Trim();
        return true;
    }

    private static string EndpointErrorMessage(string collectorType) =>
        UrlStyleCollectorTypes.Contains(collectorType)
            ? "Endpoint is required and must be an absolute http(s) URL."
            : "Endpoint is required.";

    /// <summary>
    /// Validates and trims the URL. Accepts only absolute http/https URLs so agents
    /// never receive a malformed or relative service endpoint.
    /// </summary>
    private static bool TryNormalizeUrl(string? raw, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
         || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        url = trimmed;
        return true;
    }

    /// <summary>Trims the optional label; empty/whitespace becomes null.</summary>
    private static string? NormalizeLabel(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static IResult Error(string code, string message, int statusCode) =>
        ApiError.Problem(statusCode, code, message);

    private static string EncodeCursor(DateTimeOffset createdAt, Guid targetId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt:O}|{targetId}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset? createdAt, out Guid? targetId)
    {
        createdAt = null;
        targetId = null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int sep = decoded.IndexOf('|');
            if (sep > 0
             && DateTimeOffset.TryParse(decoded[..sep], out DateTimeOffset ts)
             && Guid.TryParse(decoded[(sep + 1)..], out Guid tid))
            {
                createdAt = ts;
                targetId = tid;
                return true;
            }
        }
        catch (FormatException)
        {
            // fall through
        }

        return false;
    }
}

public sealed record TargetListItem(
    string TargetId,
    string AgentId,
    string Endpoint,
    string CollectorType,
    string? CredentialId,
    string? Label,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record TargetCandidate(
    string Endpoint,
    string CollectorType,
    string? Hostname,
    string? Vendor,
    string? Model
);

public sealed record CreateTargetRequest(
    string AgentId,
    string CollectorType,
    string Endpoint,
    string? CredentialId,
    string? Label
);

public sealed record UpdateTargetRequest(string CollectorType, string Endpoint, string? CredentialId, string? Label);