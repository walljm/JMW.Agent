using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class OuiDatabaseModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public OuiDatabaseModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public bool HasData { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public long? RecordCount { get; private set; }
    public string? VersionHash { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        (string VersionHash, DateTimeOffset UpdatedAt, long RecordCount) meta =
            await conn.GetOuiMetaAsync(ct).FirstOrDefaultAsync(ct);

        if (meta != default)
        {
            HasData = true;
            UpdatedAt = meta.UpdatedAt;
            RecordCount = meta.RecordCount;
            VersionHash = meta.VersionHash;
        }
    }
}