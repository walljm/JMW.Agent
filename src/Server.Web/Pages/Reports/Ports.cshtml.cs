using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class PortsModel : ReportPageModel
{
    private readonly ILogger<PortsModel> _logger;
    private readonly NpgsqlDataSource _db;

    public PortsModel(NpgsqlDataSource db, ILogger<PortsModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<PortListItem> Ports { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Port { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Proto { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterSortKey = null;
        string? afterDevice = null;
        string? afterListeningPort = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterDevice = parts[1];
            afterListeningPort = parts[2];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<PortListItem> items, string? next) = await PortsApi.QueryAsync(
                    _db,
                    Port,
                    Proto,
                    afterSortKey,
                    afterDevice,
                    afterListeningPort,
                    PortsApi.DefaultLimit,
                    ct,
                    Sort,
                    Dir
                );
                Ports = items;
                NextCursor = next;
            },
            ex => PortsModelLog.LoadFailed(_logger, ex),
            "_PortsTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Proto))
        {
            activeFilters["proto"] = Proto;
        }

        GridModel grid = GridModelBuilder.Build(
            "/ports",
            [
                new FilterSpec(
                    "proto",
                    "Protocol",
                    [
                        new FilterValue("tcp", "tcp"),
                        new FilterValue("tcp6", "tcp6"),
                        new FilterValue("udp", "udp"),
                        new FilterValue("udp6", "udp6"),
                    ]
                ),
            ],
            activeFilters,
            null,
            PortsApi.DefaultSort,
            Sort,
            Dir,
            PortsApi.SortableColumns,
            After,
            NextCursor,
            "#ports-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class PortsModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Ports page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}