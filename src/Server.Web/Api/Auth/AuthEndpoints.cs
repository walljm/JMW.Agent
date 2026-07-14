using System.Security.Claims;

using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Authentication;

using Npgsql;

namespace JMW.Discovery.Server.Auth;

public static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/logout", Logout);
        app.MapGet("/auth/me", Me);
        app.MapGet("/auth/oidc/login", ChallengeOidc);
    }

    private static IResult ChallengeOidc(OidcOptions oidc) =>
        oidc.Enabled
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["oidc"])
            : ApiError.Problem(404, "oidc_disabled", "SSO is not configured on this server.");

    private static async Task<IResult> Logout(
        HttpContext context,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        string? sessionId = context.Request.Cookies[SessionMiddleware.CookieName];
        if (!string.IsNullOrEmpty(sessionId))
        {
            await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
            await conn.DeleteSessionAsync(sessionId, ct).ExecuteAsync(ct);

            string username = context.User.Identity?.Name ?? "unknown";
            await audit.WriteAsync($"user:{username}", "logout", null, ct: ct);
        }

        context.Response.Cookies.Delete(SessionMiddleware.CookieName);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return ApiError.Problem(401, "unauthorized", "Not authenticated.");
        }

        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        string? username = context.User.FindFirstValue(ClaimTypes.Name);
        string? role = context.User.FindFirstValue(ClaimTypes.Role);

        return Results.Ok(
            new
            {
                user_id = userId,
                username,
                role,
            }
        );
    }
}