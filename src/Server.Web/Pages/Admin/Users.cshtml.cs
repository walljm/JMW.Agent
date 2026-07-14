using System.Security.Claims;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class UsersModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public UsersModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public List<UserRow> Users { get; private set; } = [];
    public string CurrentUserId { get; private set; } = string.Empty;
    public string AntiforgeryToken { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        CurrentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<(Guid UserId, string Username, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen)> rows =
            await conn.ListUsersAsync(ct).ToListAsync(ct);

        Users = rows.Select(r => new UserRow(
                    UserId: r.UserId.ToString(),
                    Username: r.Username,
                    Role: r.Role,
                    CreatedAt: r.CreatedAt.UtcDateTime,
                    LastSeen: r.LastSeen?.UtcDateTime
                )
            )
            .ToList();
    }

    public sealed record UserRow(
        string UserId,
        string Username,
        string Role,
        DateTime CreatedAt,
        DateTime? LastSeen
    );
}