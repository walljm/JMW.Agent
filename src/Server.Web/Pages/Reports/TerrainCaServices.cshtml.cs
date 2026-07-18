using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class TerrainCaServicesModel : ReportPageModel
{
    private readonly ILogger<TerrainCaServicesModel> _logger;
    private readonly NpgsqlDataSource _db;

    public TerrainCaServicesModel(NpgsqlDataSource db, ILogger<TerrainCaServicesModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<CaServiceItem> Cas { get; private set; } = [];
    public DataGridModel FilterBar { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
                Cas = await conn.ListCaServicesAsync(Q, ct)
                    .Select(r => new CaServiceItem(
                            r.ServiceRef,
                            r.HostName,
                            r.Status,
                            r.Address,
                            r.RootSubjectDn,
                            r.RootNotBefore?.UtcDateTime,
                            r.RootNotAfter?.UtcDateTime,
                            r.RootFingerprint,
                            r.IntSubjectDn,
                            r.IntNotBefore?.UtcDateTime,
                            r.IntNotAfter?.UtcDateTime,
                            r.ProvisionerCount
                        )
                    )
                    .ToListAsync(ct);
            },
            ex => TerrainCaServicesModelLog.LoadFailed(_logger, ex),
            "_CaServicesTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/terrain/ca-services",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            "host",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            null,
            null,
            "#ca-services-table"
        );
        FilterBar = grid.FilterBar;

        return result;
    }
}

internal static partial class TerrainCaServicesModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "CA services page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record CaServiceItem(
    string ServiceRef,
    string? HostName,
    string? Status,
    string? Address,
    string? RootSubjectDn,
    DateTime? RootNotBefore,
    DateTime? RootNotAfter,
    string? RootFingerprint,
    string? IntSubjectDn,
    DateTime? IntNotBefore,
    DateTime? IntNotAfter,
    long? ProvisionerCount
);