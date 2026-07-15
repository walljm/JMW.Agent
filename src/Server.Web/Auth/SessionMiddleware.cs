using System.Security.Claims;

using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Auth;

public sealed class SessionMiddleware
{
    private readonly RequestDelegate _next;

    public SessionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    internal const string CookieName = "jmw_session";

    public async Task InvokeAsync(HttpContext context, NpgsqlDataSource db)
    {
        string? sessionId = context.Request.Cookies[CookieName];

        if (!string.IsNullOrEmpty(sessionId))
        {
            ClaimsPrincipal? principal = await LoadSessionAsync(db, sessionId, context.RequestAborted);
            if (principal is not null)
            {
                context.User = principal;
            }
        }

        await _next(context);
    }

    private static async Task<ClaimsPrincipal?> LoadSessionAsync(
        NpgsqlDataSource db,
        string sessionId,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        (Guid UserId, string Username, string Role) row = await conn.LoadSessionAsync(sessionId, ct)
            .FirstOrDefaultAsync(ct);

        if (row == default)
        {
            return null;
        }

        Claim[] claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, row.UserId.ToString()),
            new Claim(ClaimTypes.Name, row.Username),
            new Claim(ClaimTypes.Role, row.Role),
            new Claim("session_id", sessionId),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "jmw-session"));
    }
}