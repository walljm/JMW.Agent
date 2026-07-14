using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Pages.Reports;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Fleet;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class OverviewModel : PageModel
{
    private readonly ILogger<OverviewModel> _logger;
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public OverviewModel(IAntiforgery antiforgery, NpgsqlDataSource db, ILogger<OverviewModel> logger)
    {
        _antiforgery = antiforgery;
        _db = db;
        _logger = logger;
    }

    private const int NeedsAttentionCap = 5;

    public AgentHealthVm? Health { get; private set; }
    public List<AgentListItem> Pending { get; private set; } = [];
    public List<VersionCount> VersionHistogram { get; private set; } = [];
    public string? LoadError { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        try
        {
            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

            (long? Total, long? Approved, long? Pending, long? Online, long? Stale, long? Offline) s =
                await conn.GetAgentHealthSummaryAsync(ct).FirstOrDefaultAsync(ct);

            List<AgentRow> agents = [];
            await foreach ((Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone,
                string? Version, string? PassiveDiscoveryMode, string? Liveness) a in
                conn.GetAgentHealthListAsync(NeedsAttentionCap, ct))
            {
                // GetAgentHealthListAsync is shared with the Dashboard's generic "Agents" panel and
                // has no liveness filter of its own — it pads its capped result with healthy agents
                // when there aren't enough unhealthy ones. This panel is titled "Needs Attention", so
                // only keep the agents that actually need it.
                if (a.Liveness == "online")
                {
                    continue;
                }

                agents.Add(
                    new AgentRow(
                        a.AgentId,
                        a.Hostname,
                        a.Status,
                        a.LastHeartbeat,
                        a.Zone,
                        a.Version,
                        a.PassiveDiscoveryMode,
                        a.Liveness ?? "offline"
                    )
                );
            }

            Health = new AgentHealthVm(
                s.Total ?? 0,
                s.Approved ?? 0,
                s.Pending ?? 0,
                s.Online ?? 0,
                s.Stale ?? 0,
                s.Offline ?? 0,
                agents
            );

            (List<AgentListItem> pendingItems, _) = await AgentsApi.QueryAsync(
                _db,
                "pending",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                200,
                ct
            );
            Pending = pendingItems;

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT coalesce(version, 'unknown') AS version, count(*) AS agent_count
                FROM agents
                GROUP BY coalesce(version, 'unknown')
                ORDER BY count(*) DESC, version ASC
                """;
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                VersionHistogram.Add(new VersionCount(reader.GetString(0), reader.GetInt64(1)));
            }
        }
        catch (NpgsqlException ex)
        {
            LoadError = ReportPageModel.SafeLoadErrorMessage;
            OverviewModelLog.LoadFailed(_logger, ex);
        }
    }
}

internal static partial class OverviewModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Fleet overview page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record VersionCount(string Version, long Count);