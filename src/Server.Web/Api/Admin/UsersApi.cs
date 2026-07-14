using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

public static class UsersApi
{
    public static readonly IReadOnlySet<string> ValidRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin",
        "viewer",
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/users", ListUsers)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPatch("/users/{id}/role", UpdateRole)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapDelete("/users/{id}", DeleteUser)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> ListUsers(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<UserListItem> users = await conn.ListUsersAsync(ct)
            .Select(r => new UserListItem(
                    UserId: r.UserId.ToString(),
                    Username: r.Username,
                    Role: r.Role,
                    CreatedAt: r.CreatedAt.UtcDateTime,
                    LastSeen: r.LastSeen?.UtcDateTime
                )
            )
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    private static async Task<IResult> UpdateRole(
        string id,
        UpdateUserRoleRequest request,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid userGuid))
        {
            return ApiError.InvalidId("Invalid user id.");
        }

        if (!ValidRoles.Contains(request.Role))
        {
            return ApiError.InvalidRequest("Role must be 'admin' or 'viewer'.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        // Demoting the last admin would lock everyone out of admin-only pages (including this
        // one) with no way back in short of a direct DB edit — block it regardless of who's
        // asking, including self-demotion.
        if (string.Equals(request.Role, "viewer", StringComparison.Ordinal))
        {
            List<(Guid UserId, string Username, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen)>
                targetRows = await conn.ListUsersAsync(ct).Where(r => r.UserId == userGuid).ToListAsync(ct);

            if (targetRows.Count > 0 && string.Equals(targetRows[0].Role, "admin", StringComparison.Ordinal))
            {
                AdminCountResult adminCount = await conn.CountAdminsAsync(ct).FirstOrDefaultAsync(ct);
                if (adminCount.Count is 1)
                {
                    return ApiError.InvalidRequest("Cannot demote the last remaining admin.");
                }
            }
        }

        UserIdResult result = await conn.UpdateUserRoleAsync(userGuid, request.Role, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("User not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "user.role.set", id, ct: ct);

        return Results.Ok(
            new
            {
                role = request.Role,
            }
        );
    }

    private static async Task<IResult> DeleteUser(
        string id,
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid userGuid))
        {
            return ApiError.InvalidId("Invalid user id.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(Guid UserId, string Username, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen)> rows =
            await conn.ListUsersAsync(ct).Where(r => r.UserId == userGuid).ToListAsync(ct);

        if (rows.Count == 0)
        {
            return ApiError.NotFound("User not found.");
        }

        if (string.Equals(rows[0].Role, "admin", StringComparison.Ordinal))
        {
            AdminCountResult adminCount = await conn.CountAdminsAsync(ct).FirstOrDefaultAsync(ct);
            if (adminCount.Count is 1)
            {
                return ApiError.InvalidRequest("Cannot delete the last remaining admin.");
            }
        }

        UserIdResult result = await conn.DeleteUserAsync(userGuid, ct).FirstOrDefaultAsync(ct);

        if (result == default)
        {
            return ApiError.NotFound("User not found.");
        }

        string actor = context.User.Identity?.Name ?? "admin";
        await audit.WriteAsync($"user:{actor}", "user.delete", id, ct: ct);

        return Results.NoContent();
    }
}

public sealed record UserListItem(
    string UserId,
    string Username,
    string Role,
    DateTime CreatedAt,
    DateTime? LastSeen
);

public sealed record UpdateUserRoleRequest(string Role);