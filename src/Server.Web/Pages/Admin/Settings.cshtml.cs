using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class SettingsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public SettingsModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public List<RetentionPolicyRow> RetentionPolicies { get; private set; } = [];
    public int OnlineMultiplier { get; private set; }
    public int OfflineCeilingSecs { get; private set; }
    public int DeviceLivenessWindowHours { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        List<(string TableName, string Category, string TimeColumn, TimeSpan? StaleAfter, bool Enabled, string? Notes)>
            rows =
                await conn.ListAllRetentionPoliciesAsync(ct).ToListAsync(ct);

        RetentionPolicies = rows.Select(r => new RetentionPolicyRow(
                    r.TableName,
                    r.Category,
                    r.TimeColumn,
                    r.StaleAfter.HasValue ? (long)r.StaleAfter.Value.TotalSeconds : null,
                    r.Enabled,
                    r.Notes
                )
            )
            .ToList();

        (int OnlineMultiplier, int OfflineCeilingSecs) livenessSettings =
            await conn.GetAgentLivenessSettingsAsync(ct).FirstAsync(ct);
        OnlineMultiplier = livenessSettings.OnlineMultiplier;
        OfflineCeilingSecs = livenessSettings.OfflineCeilingSecs;

        DeviceLivenessWindowHours = (await conn.GetDeviceLivenessSettingsAsync(ct).FirstAsync(ct)).WindowHours;
    }

    public sealed record RetentionPolicyRow(
        string TableName,
        string Category,
        string TimeColumn,
        long? StaleAfterSecs,
        bool Enabled,
        string? Notes
    );
}