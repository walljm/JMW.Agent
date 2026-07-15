using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class TerrainCaModel : ReportPageModel
{
    private readonly ILogger<TerrainCaModel> _logger;
    private readonly NpgsqlDataSource _db;

    public TerrainCaModel(NpgsqlDataSource db, ILogger<TerrainCaModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<TerrainCaItem> Cas { get; private set; } = [];
    public DataGridModel FilterBar { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        IActionResult result = await RunReportLoadAsync(
            async () =>
            {
                await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
                Cas = await conn.ListTerrainCaInventoryAsync(Q, ct)
                    .Select(r => new TerrainCaItem(
                            r.Kind ?? "unknown",
                            r.Subtype,
                            r.SubjectDn,
                            r.Fingerprint,
                            r.NotBefore?.UtcDateTime,
                            r.NotAfter?.UtcDateTime,
                            r.ServiceRef,
                            r.SeenOnCount
                        )
                    )
                    .ToListAsync(ct);
            },
            ex => TerrainCaModelLog.LoadFailed(_logger, ex),
            "_TerrainCaTable"
        );

        GridModel grid = GridModelBuilder.Build(
            "/terrain/ca",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Q,
            "kind",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            null,
            null,
            "#ca-table"
        );
        FilterBar = grid.FilterBar;

        return result;
    }
}

internal static partial class TerrainCaModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Terrain CA page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record TerrainCaItem(
    string Kind,
    string? Subtype,
    string? SubjectDn,
    string? Fingerprint,
    DateTime? NotBefore,
    DateTime? NotAfter,
    string? ServiceRef,
    long? SeenOnCount
);
