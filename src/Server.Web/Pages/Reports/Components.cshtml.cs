using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ComponentsModel : ReportPageModel
{
    private readonly ILogger<ComponentsModel> _logger;
    private readonly NpgsqlDataSource _db;

    public ComponentsModel(NpgsqlDataSource db, ILogger<ComponentsModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<ComponentListItem> Components { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Class { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterSortKey = null;
        string? afterDevice = null;
        string? afterComponent = null;
        if (!string.IsNullOrEmpty(After) && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
            afterComponent = parts[2];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<ComponentListItem> items, string? next) = await ComponentsApi.QueryAsync(
                    _db,
                    Q,
                    Class,
                    afterSortKey,
                    afterDevice,
                    afterComponent,
                    ComponentsApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Components = items;
                NextCursor = next;
            },
            ex => ComponentsModelLog.LoadFailed(_logger, ex),
            "_ComponentsTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Class))
        {
            activeFilters["class"] = Class;
        }

        GridModel grid = GridModelBuilder.Build(
            "/components",
            [
                new FilterSpec(
                    "class",
                    "Class",
                    [
                        new FilterValue("board", "Board"),
                        new FilterValue("cpu", "CPU"),
                        new FilterValue("memory", "Memory"),
                        new FilterValue("disk", "Disk"),
                        new FilterValue("psu", "Power Supply"),
                        new FilterValue("fan", "Fan"),
                    ]
                ),
            ],
            activeFilters,
            Q,
            ComponentsApi.DefaultSort,
            Sort,
            Dir,
            ComponentsApi.SortableColumns,
            After,
            NextCursor,
            "#components-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class ComponentsModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Components page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}
