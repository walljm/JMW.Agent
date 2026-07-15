using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

/// <summary>Admin schema management for custom_field_definitions (docs/plans/user-provided.md).
/// Per-device values are entered from each device's detail page, not here.</summary>
[Authorize(Policy = RbacPolicies.Admin)]
public sealed class CustomFieldsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public CustomFieldsModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public string AntiforgeryToken { get; private set; } = string.Empty;
    public List<CustomFieldDefinition> Definitions { get; private set; } = [];
    public IReadOnlyList<string> AttachableViews { get; private set; } = [];
    public IReadOnlyList<string> ViewGroups { get; } = Enum.GetNames<FactViewGroup>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        Definitions = await conn.ListCustomFieldDefinitionsAsync(ct)
            .Select(r => new CustomFieldDefinition(r.Id, r.Label, r.Slug, r.TargetViewTitle, r.TargetViewGroup,
                    r.IsNewView, r.CreatedAt, r.CreatedBy
                )
            )
            .ToListAsync(ct);

        AttachableViews = FactViewLibrary.All
            .Where(v => v.Kind == FactViewKind.Properties)
            .Select(v => v.Title)
            .ToList();
    }
}