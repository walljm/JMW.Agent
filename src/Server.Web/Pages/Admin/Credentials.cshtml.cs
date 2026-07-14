using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class CredentialsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public CredentialsModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public List<CredentialListItem> Credentials { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    private const int PageSize = 100;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterCredentialId = null;

        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 2, out string[] parts)
         && DateTimeOffset.TryParse(parts[0], out DateTimeOffset ts)
         && Guid.TryParse(parts[1], out Guid id))
        {
            afterCreatedAt = ts;
            afterCredentialId = id;
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)> rows =
            await conn.ListCredentialsAsync(Type, afterCreatedAt, afterCredentialId, PageSize + 1, ct)
                .ToListAsync(ct);

        if (rows.Count > PageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            (Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) last =
                rows[rows.Count - 1];
            NextCursor = KeysetCursor.EncodeParts(last.CreatedAt.ToString("O"), last.CredentialId.ToString());
        }

        Credentials = rows.Select(r => new CredentialListItem(
                    CredentialId: r.CredentialId.ToString(),
                    Name: r.Name,
                    Type: r.Type,
                    CreatedAt: r.CreatedAt.UtcDateTime,
                    UpdatedAt: r.UpdatedAt.UtcDateTime
                )
            )
            .ToList();

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Type))
        {
            activeFilters["type"] = Type;
        }

        GridModel grid = GridModelBuilder.Build(
            "/admin/credentials",
            [
                new FilterSpec(
                    "type",
                    "Type",
                    [
                        new FilterValue("ssh-key", "ssh-key"),
                        new FilterValue("ssh-password", "ssh-password"),
                        new FilterValue("snmp", "snmp"),
                        new FilterValue("api-token", "api-token"),
                    ]
                ),
            ],
            activeFilters,
            null,
            "created_at",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#credentials-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        if (string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial("_CredentialsTable", this);
        }

        return Page();
    }
}