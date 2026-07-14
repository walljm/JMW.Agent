using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ArpModel : ReportPageModel
{
    private readonly ILogger<ArpModel> _logger;
    private readonly NpgsqlDataSource _db;

    public ArpModel(NpgsqlDataSource db, ILogger<ArpModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<ArpListItem> Entries { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

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
        string? afterArp = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
            afterArp = parts[2];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<ArpListItem> items, string? next) = await ArpApi.QueryAsync(
                    _db,
                    Q,
                    afterSortKey,
                    afterDevice,
                    afterArp,
                    ArpApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Entries = items;
                NextCursor = next;
            },
            ex => ArpModelLog.LoadFailed(_logger, ex),
            "_ArpTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);

        GridModel grid = GridModelBuilder.Build(
            "/arp",
            [],
            activeFilters,
            Q,
            ArpApi.DefaultSort,
            Sort,
            Dir,
            ArpApi.SortableColumns,
            After,
            NextCursor,
            "#arp-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class ArpModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Arp page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}