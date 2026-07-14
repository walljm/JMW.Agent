using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ContainersModel : ReportPageModel
{
    private readonly ILogger<ContainersModel> _logger;
    private readonly NpgsqlDataSource _db;

    public ContainersModel(NpgsqlDataSource db, ILogger<ContainersModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<ContainerListItem> Containers { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Image { get; set; }

    // Q is used as a free-text search; maps to image name search in the API.
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterSortKey = null;
        string? afterDevice = null;
        string? afterContainer = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
            afterContainer = parts[2];
        }

        // FilterBar sends free-text search as "q"; map to image search.
        string? imageSearch = Image ?? Q;

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<ContainerListItem> items, string? next) = await ContainersApi.QueryAsync(
                    _db,
                    State,
                    imageSearch,
                    afterSortKey,
                    afterDevice,
                    afterContainer,
                    ContainersApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Containers = items;
                NextCursor = next;
            },
            ex => ContainersModelLog.LoadFailed(_logger, ex),
            "_ContainersTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(State))
        {
            activeFilters["state"] = State;
        }

        GridModel grid = GridModelBuilder.Build(
            "/containers",
            [
                new FilterSpec(
                    "state",
                    "State",
                    [
                        new FilterValue("running", "Running"),
                        new FilterValue("exited", "Exited"),
                        new FilterValue("paused", "Paused"),
                        new FilterValue("restarting", "Restarting"),
                        new FilterValue("dead", "Dead"),
                    ]
                ),
            ],
            activeFilters,
            imageSearch,
            ContainersApi.DefaultSort,
            Sort,
            Dir,
            ContainersApi.SortableColumns,
            After,
            NextCursor,
            "#containers-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class ContainersModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Containers page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}