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

    private const int PreviewLimit = 5;

    public TerrainSummary Summary { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    public string? SummaryLoadError { get; private set; }

    /// <summary>Top DNS services by query volume — the "what's actually talking" preview under
    /// the DNS cards, linking through to /terrain/dns for the full list.</summary>
    public IReadOnlyList<TerrainDnsService> DnsPreview { get; private set; } = [];

    /// <summary>All DHCP scopes (rarely more than a handful) — the same set /terrain/dhcp shows,
    /// just not paginated here.</summary>
    public IReadOnlyList<TerrainDhcpScope> DhcpPreview { get; private set; } = [];

    /// <summary>Certs soonest-to-expire first, so the preview surfaces exactly what the
    /// "expiring within 30 days" card language is warning about.</summary>
    public IReadOnlyList<TerrainCaItem> CaPreview { get; private set; } = [];

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

            DnsPreview = await conn.ListTerrainDnsServicesAsync(ct)
                .Select(r => new TerrainDnsService(r.Service, r.TotalQueries, r.TotalBlocked, r.BlockedPct, r.UpdatedAt.UtcDateTime))
                .OrderByDescending(s => s.TotalQueries ?? 0)
                .Take(PreviewLimit)
                .ToListAsync(ct);

            DhcpPreview = await conn.ListTerrainDhcpScopesAsync(ct)
                .Select(r => new TerrainDhcpScope(r.Service, r.Scope, r.Enabled, r.StartAddress, r.EndAddress, r.SubnetMask, r.Gateway))
                .Take(PreviewLimit)
                .ToListAsync(ct);

            CaPreview = await conn.ListTerrainCaInventoryAsync(null, ct)
                .Select(r => new TerrainCaItem(r.Kind ?? "unknown", r.Subtype, r.SubjectDn, r.Fingerprint, r.NotBefore?.UtcDateTime, r.NotAfter?.UtcDateTime, r.ServiceRef, r.SeenOnCount))
                .OrderBy(c => c.NotAfter ?? DateTime.MaxValue)
                .Take(PreviewLimit)
                .ToListAsync(ct);
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