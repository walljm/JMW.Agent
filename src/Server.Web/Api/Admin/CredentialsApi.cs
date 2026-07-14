using System.Text;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class CredentialsApi
{
    private const int DefaultPageLimit = 100;

    // Credential store types — must stay in sync with the schema comment and UI dropdown.
    private static readonly HashSet<string> ValidTypes =
        new(StringComparer.Ordinal)
        {
            "ssh-key",
            "ssh-password",
            "snmp",
            "api-token",
        };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/credentials", ListCredentials)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/credentials", CreateCredential)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/credentials/{id}", UpdateCredential)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPut("/credentials/{id}/secret", RotateSecret)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapDelete("/credentials/{id}", DeleteCredential)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> ListCredentials(
        NpgsqlDataSource db,
        string? after,
        string? type,
        int limit = DefaultPageLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, 200);

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterCredentialId = null;

        if (!string.IsNullOrEmpty(after))
        {
            if (!TryDecodeCursor(after, out afterCreatedAt, out afterCredentialId))
            {
                return Error("invalid_cursor", "Invalid pagination cursor.", 422);
            }
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)> rows =
            await conn.ListCredentialsAsync(type, afterCreatedAt, afterCredentialId, limit + 1, ct)
                .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            (Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) last =
                rows[rows.Count - 1];
            nextCursor = EncodeCursor(last.CreatedAt, last.CredentialId);
        }

        List<CredentialListItem> items = rows.Select(r => new CredentialListItem(
                    CredentialId: r.CredentialId.ToString(),
                    Name: r.Name,
                    Type: r.Type,
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

    private static async Task<IResult> CreateCredential(
        CreateCredentialRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        CredentialProtector protector,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error("invalid_name", "Name is required.", 422);
        }

        if (!ValidTypes.Contains(request.Type))
        {
            return Error("invalid_type", $"Unknown credential type '{request.Type}'.", 422);
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Error("invalid_secret", "Secret is required.", 422);
        }

        // Trim stray leading/trailing whitespace from copy-paste — a token/community string/
        // password never legitimately carries it, and an extra newline silently turns an
        // otherwise-valid secret into one the target rejects.
        byte[] blob = protector.Encrypt(request.Secret.Trim());

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        (Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt) inserted = await conn
            .InsertCredentialAsync(request.Name, request.Type, blob, ct)
            .FirstOrDefaultAsync(ct);

        if (inserted == default)
        {
            return Error("create_failed", "Failed to create credential.", 500);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "credential.create", inserted.CredentialId.ToString(), ct: ct);

        return Results.Ok(
            new
            {
                credential_id = inserted.CredentialId.ToString(),
                name = inserted.Name,
                type = inserted.Type,
                created_at = inserted.CreatedAt.UtcDateTime,
            }
        );
    }

    private static async Task<IResult> UpdateCredential(
        string id,
        UpdateCredentialRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid credentialId))
        {
            return Error("invalid_id", "Invalid credential id.", 400);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error("invalid_name", "Name is required.", 422);
        }

        if (!ValidTypes.Contains(request.Type))
        {
            return Error("invalid_type", $"Unknown credential type '{request.Type}'.", 422);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        CredentialIdResult result = await conn
            .UpdateCredentialMetaAsync(credentialId, request.Name, request.Type, ct)
            .FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Credential not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "credential.update", id, ct: ct);

        return Results.Ok(
            new
            {
                credential_id = id,
                name = request.Name,
                type = request.Type,
            }
        );
    }

    private static async Task<IResult> RotateSecret(
        string id,
        RotateSecretRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        CredentialProtector protector,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid credentialId))
        {
            return Error("invalid_id", "Invalid credential id.", 400);
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            return Error("invalid_secret", "Secret is required.", 422);
        }

        // Trim stray leading/trailing whitespace from copy-paste — see CreateCredential.
        byte[] blob = protector.Encrypt(request.Secret.Trim());

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        CredentialIdResult result = await conn
            .UpdateCredentialSecretAsync(credentialId, blob, ct)
            .FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return Error("not_found", "Credential not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "credential.rotate", id, ct: ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteCredential(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid credentialId))
        {
            return Error("invalid_id", "Invalid credential id.", 400);
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        // Distinguish 409 (in use) from 404 (not found): the DELETE returns no rows
        // for both, so check usage first.
        InUseResult inUse = await conn.IsCredentialInUseAsync(credentialId, ct).FirstOrDefaultAsync(ct);
        if (inUse.InUse == true)
        {
            return Error(
                "credential_in_use",
                "Credential is referenced by one or more collection or service targets.",
                409
            );
        }

        CredentialIdResult result = await conn.DeleteCredentialAsync(credentialId, ct).FirstOrDefaultAsync(ct);
        if (result == default)
        {
            return Error("not_found", "Credential not found.", 404);
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "credential.delete", id, ct: ct);

        return Results.NoContent();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IResult Error(string code, string message, int statusCode) =>
        ApiError.Problem(statusCode, code, message);

    private static string EncodeCursor(DateTimeOffset createdAt, Guid credentialId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt:O}|{credentialId}"));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset? createdAt, out Guid? credentialId)
    {
        createdAt = null;
        credentialId = null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int sep = decoded.IndexOf('|');
            if (sep > 0
             && DateTimeOffset.TryParse(decoded[..sep], out DateTimeOffset ts)
             && Guid.TryParse(decoded[(sep + 1)..], out Guid cid))
            {
                createdAt = ts;
                credentialId = cid;
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

public sealed record CredentialListItem(
    string CredentialId,
    string Name,
    string Type,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record CreateCredentialRequest(string Name, string Type, string Secret);

public sealed record UpdateCredentialRequest(string Name, string Type);

public sealed record RotateSecretRequest(string Secret);