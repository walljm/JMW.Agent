using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class TerrainDhcpModel : ReportPageModel
{
    private readonly ILogger<TerrainDhcpModel> _logger;
    private readonly NpgsqlDataSource _db;

    public TerrainDhcpModel(NpgsqlDataSource db, ILogger<TerrainDhcpModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<DhcpLeaseItem> Leases { get; private set; } = [];
    public IReadOnlyList<TerrainDhcpScope> Scopes { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string? afterDevice = null;
        string? afterLease = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 2, out string[] parts))
        {
            afterDevice = parts[0];
            afterLease = parts[1];
        }

        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<DhcpLeaseItem> items, string? next) = await TerrainApi.QueryDhcpAsync(
                    _db,
                    Q,
                    afterDevice,
                    afterLease,
                    TerrainApi.DefaultLimit,
                    ct
                );
                Leases = items;
                NextCursor = next;

                await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
                Scopes = await conn.ListTerrainDhcpScopesAsync(ct)
                    .Select(r => new TerrainDhcpScope(
                            r.Service,
                            r.Scope,
                            r.Enabled,
                            r.StartAddress,
                            r.EndAddress,
                            r.SubnetMask,
                            r.Gateway
                        )
                    )
                    .ToListAsync(ct);
            },
            ex => TerrainDhcpModelLog.LoadFailed(_logger, ex),
            "_TerrainDhcpTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/terrain/dhcp",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            "device",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#dhcp-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        return result;
    }
}

internal static partial class TerrainDhcpModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Terrain DHCP page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record TerrainDhcpScope(
    string Service,
    string Scope,
    bool? Enabled,
    string? StartAddress,
    string? EndAddress,
    string? SubnetMask,
    string? Gateway
);