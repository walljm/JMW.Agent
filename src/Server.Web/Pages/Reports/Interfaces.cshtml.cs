using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class InterfacesModel : ReportPageModel
{
    private readonly ILogger<InterfacesModel> _logger;
    private readonly NpgsqlDataSource _db;

    public InterfacesModel(NpgsqlDataSource db, ILogger<InterfacesModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<InterfaceListItem> Interfaces { get; private set; } = [];
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
        string? afterInterface = null;
        if (!string.IsNullOrEmpty(After) && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
            afterInterface = parts[2];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<InterfaceListItem> items, string? next) = await InterfacesApi.QueryAsync(
                    _db,
                    Q,
                    afterSortKey,
                    afterDevice,
                    afterInterface,
                    InterfacesApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Interfaces = items;
                NextCursor = next;
            },
            ex => InterfacesModelLog.LoadFailed(_logger, ex),
            "_InterfacesTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/interfaces",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            InterfacesApi.DefaultSort,
            Sort,
            Dir,
            InterfacesApi.SortableColumns,
            After,
            NextCursor,
            "#interfaces-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class InterfacesModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Interfaces page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}