using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class TerrainModel : PageModel
{
    private readonly ILogger<TerrainModel> _logger;
    private readonly NpgsqlDataSource _db;

    public TerrainModel(NpgsqlDataSource db, ILogger<TerrainModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public TerrainSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    public string? SummaryLoadError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
            (long? DnsServerCount, long? TotalQueries, long? TotalBlocked, long? ActiveScopeCount,
                long? LocalLeaseCount, long? CaCount, long? CaExpiringCount) row =
                    await conn.GetTerrainSummaryAsync(ct).FirstOrDefaultAsync(ct);
            Summary = new TerrainSummary(
                row.DnsServerCount ?? 0,
                row.TotalQueries ?? 0,
                row.TotalBlocked ?? 0,
                row.ActiveScopeCount ?? 0,
                row.LocalLeaseCount ?? 0,
                row.CaCount ?? 0,
                row.CaExpiringCount ?? 0
            );
        }
        catch (NpgsqlException ex)
        {
            SummaryLoadError = ReportPageModel.SafeLoadErrorMessage;
            TerrainModelLog.SummaryLoadFailed(_logger, ex);
        }
    }
}

internal static partial class TerrainModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Terrain summary load failed.")]
    public static partial void SummaryLoadFailed(ILogger logger, Exception ex);
}

public sealed record TerrainSummary(
    long DnsServerCount,
    long TotalQueries,
    long TotalBlocked,
    long ActiveScopeCount,
    long LocalLeaseCount,
    long CaCount,
    long CaExpiringCount
);