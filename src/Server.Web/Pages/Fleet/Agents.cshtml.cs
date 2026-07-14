using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Fleet;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class AgentsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public AgentsModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public List<AgentListItem> Agents { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Zone { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Version { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Liveness { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        string? afterSortKey = null;
        string? afterCreatedAt = null;
        string? afterAgentId = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterCreatedAt = parts[1];
            afterAgentId = parts[2];
        }

        (List<AgentListItem> items, string? next) = await AgentsApi.QueryAsync(
            _db,
            Status,
            Zone,
            Version,
            Liveness,
            Q,
            afterSortKey,
            afterCreatedAt,
            afterAgentId,
            AgentsApi.DefaultLimit,
            ct,
            Sort,
            Dir
        );
        Agents = items;
        NextCursor = next;

        (List<string> zones, List<string> versions) = await AgentsApi.GetFilterFacetsAsync(_db, ct);

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Status))
        {
            activeFilters["status"] = Status;
        }

        if (!string.IsNullOrEmpty(Zone))
        {
            activeFilters["zone"] = Zone;
        }

        if (!string.IsNullOrEmpty(Version))
        {
            activeFilters["version"] = Version;
        }

        if (!string.IsNullOrEmpty(Liveness))
        {
            activeFilters["liveness"] = Liveness;
        }

        GridModel grid = GridModelBuilder.Build(
            "/fleet/agents",
            [
                new FilterSpec(
                    "status",
                    "Status",
                    [
                        new FilterValue("pending", "Pending"),
                        new FilterValue("approved", "Approved"),
                        new FilterValue("disabled", "Disabled"),
                    ]
                ),
                new FilterSpec(
                    "liveness",
                    "Liveness",
                    AgentsApi.LivenessValues.Select(v => new FilterValue(v, v)).ToList()
                ),
                new FilterSpec(
                    "zone",
                    "Zone",
                    zones.Select(z => new FilterValue(z, z)).ToList()
                ),
                new FilterSpec(
                    "version",
                    "Version",
                    versions.Select(v => new FilterValue(v, v)).ToList()
                ),
            ],
            activeFilters,
            Q,
            AgentsApi.DefaultSort,
            Sort,
            Dir,
            AgentsApi.SortableColumns,
            After,
            NextCursor,
            "#agents-table",
            AgentsApi.DefaultDir
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        if (string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial("_AgentsTable", this);
        }

        return Page();
    }
}