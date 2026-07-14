using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class HardwareModel : ReportPageModel
{
    private readonly ILogger<HardwareModel> _logger;
    private readonly NpgsqlDataSource _db;

    public HardwareModel(NpgsqlDataSource db, ILogger<HardwareModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<HardwareListItem> Hardware { get; private set; } = [];
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
        if (!string.IsNullOrEmpty(After) && KeysetCursor.TryDecodeParts(After, 2, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<HardwareListItem> items, string? next) = await HardwareApi.QueryAsync(
                    _db,
                    Q,
                    afterSortKey,
                    afterDevice,
                    HardwareApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Hardware = items;
                NextCursor = next;
            },
            ex => HardwareModelLog.LoadFailed(_logger, ex),
            "_HardwareTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/hardware",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            HardwareApi.DefaultSort,
            Sort,
            Dir,
            HardwareApi.SortableColumns,
            After,
            NextCursor,
            "#hardware-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class HardwareModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Hardware page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}
