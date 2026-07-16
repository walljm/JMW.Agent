using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class DevicesModel : ReportPageModel
{
    private readonly ILogger<DevicesModel> _logger;
    private readonly NpgsqlDataSource _db;

    public DevicesModel(NpgsqlDataSource db, ILogger<DevicesModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<DeviceReportItem> Devices { get; private set; } = [];

    /// <summary>Operator-fact report columns (fact_path_metadata.show_in_reports) and this page's
    /// values, keyed (deviceId, attributePath). Display-only — never sortable/filterable.</summary>
    public IReadOnlyList<OperatorFactColumns.Column> OperatorColumns { get; private set; } = [];

    public IReadOnlyDictionary<(string Device, string Path), string> OperatorValues { get; private set; } =
        new Dictionary<(string, string), string>();

    public string? NextCursor { get; private set; }
    public string? PrevCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Os { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Vendor { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    /// <summary>The active sort column, resolved to the default when unset/invalid.</summary>
    public string ActiveSort => DeviceListApi.IsSortable(Sort) ? Sort! : DeviceListApi.DefaultSort;

    /// <summary>The active direction ("asc"/"desc"), defaulting to ascending.</summary>
    public string ActiveDir => string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        PrevCursor = string.IsNullOrEmpty(After) ? null : string.Empty;

        string? afterSortKey = null;
        string? afterDeviceId = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecode(After, out string sortKey, out string deviceId))
        {
            afterSortKey = sortKey;
            afterDeviceId = deviceId;
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<DeviceReportItem> items, string? next) = await DeviceListApi.QueryAsync(
                    _db,
                    Status,
                    Source,
                    Os,
                    Vendor,
                    Q,
                    afterSortKey,
                    afterDeviceId,
                    DeviceListApi.DefaultLimit,
                    ct,
                    ActiveSort,
                    ActiveDir
                );

                Devices = items;
                NextCursor = next;

                (OperatorColumns, OperatorValues) = await OperatorFactColumns.LoadAsync(
                    _db,
                    [.. items.Select(i => i.DeviceId)],
                    ct
                );
            },
            ex => DevicesModelLog.LoadFailed(_logger, ex),
            "_DevicesTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Status))
        {
            activeFilters["status"] = Status;
        }

        if (!string.IsNullOrEmpty(Source))
        {
            activeFilters["source"] = Source;
        }

        if (!string.IsNullOrEmpty(Os))
        {
            activeFilters["os"] = Os;
        }

        if (!string.IsNullOrEmpty(Vendor))
        {
            activeFilters["vendor"] = Vendor;
        }

        GridModel grid = GridModelBuilder.Build(
            "/devices",
            [
                new FilterSpec(
                    "status",
                    "Status",
                    [
                        new FilterValue("managed", "Managed"),
                        new FilterValue("discovered", "Discovered"),
                    ]
                ),
                new FilterSpec(
                    "source",
                    "Source",
                    [
                        new FilterValue("agent", "Agent"),
                        new FilterValue("arp", "ARP"),
                        new FilterValue("mdns", "mDNS"),
                        new FilterValue("lldp", "LLDP"),
                        new FilterValue("snmp", "SNMP"),
                    ]
                ),
            ],
            activeFilters,
            Q,
            DeviceListApi.DefaultSort,
            Sort,
            Dir,
            DeviceListApi.SortableColumns,
            After,
            NextCursor,
            "#devices-table"
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class DevicesModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Devices page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}