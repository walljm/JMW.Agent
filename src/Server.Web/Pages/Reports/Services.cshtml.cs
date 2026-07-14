using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ServicesModel : ReportPageModel
{
    private readonly ILogger<ServicesModel> _logger;
    private readonly NpgsqlDataSource _db;

    public ServicesModel(NpgsqlDataSource db, ILogger<ServicesModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<ServiceListItem> Services { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterService = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 1, out string[] parts))
        {
            afterService = parts[0];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<ServiceListItem> items, string? next) = await ServicesApi.QueryAsync(
                    _db,
                    Type,
                    Q,
                    afterService,
                    ServicesApi.DefaultLimit,
                    ct
                );
                Services = items;
                NextCursor = next;
            },
            ex => ServicesModelLog.LoadFailed(_logger, ex),
            "_ServicesTable"
        );

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Type))
        {
            activeFilters["type"] = Type;
        }

        GridModel grid = GridModelBuilder.Build(
            "/services",
            [
                new FilterSpec(
                    "type",
                    "Type",
                    [
                        new FilterValue("technitium-dns", "Technitium DNS"),
                        new FilterValue("home-assistant", "Home Assistant"),
                    ]
                ),
            ],
            activeFilters,
            Q,
            "service",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#services-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class ServicesModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Services page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}