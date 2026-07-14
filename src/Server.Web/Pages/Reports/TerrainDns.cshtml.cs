using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class TerrainDnsModel : ReportPageModel
{
    private readonly ILogger<TerrainDnsModel> _logger;
    private readonly NpgsqlDataSource _db;

    public TerrainDnsModel(NpgsqlDataSource db, ILogger<TerrainDnsModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<DnsRecordItem> Records { get; private set; } = [];
    public IReadOnlyList<TerrainDnsService> Services { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterService = null;
        string? afterZone = null;
        string? afterRecord = null;
        string? afterRtype = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 4, out string[] parts))
        {
            afterService = parts[0];
            afterZone = parts[1];
            afterRecord = parts[2];
            afterRtype = parts[3];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<DnsRecordItem> items, string? next) = await TerrainApi.QueryDnsAsync(
                    _db,
                    Q,
                    afterService,
                    afterZone,
                    afterRecord,
                    afterRtype,
                    TerrainApi.DefaultLimit,
                    ct
                );
                Records = items;
                NextCursor = next;

                await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
                Services = await conn.ListTerrainDnsServicesAsync(ct)
                    .Select(r => new TerrainDnsService(r.Service, r.TotalQueries, r.TotalBlocked, r.BlockedPct, r.UpdatedAt.UtcDateTime))
                    .ToListAsync(ct);
            },
            ex => TerrainDnsModelLog.LoadFailed(_logger, ex),
            "_TerrainDnsTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/terrain/dns",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            "service",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#dns-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class TerrainDnsModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Terrain DNS page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record TerrainDnsService(
    string Service,
    long? TotalQueries,
    long? TotalBlocked,
    double? BlockedPct,
    DateTime UpdatedAt
);