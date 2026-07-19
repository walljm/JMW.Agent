using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Pages.Reports;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Fleet;

/// <summary>
/// The Fleet page (/fleet). Combines the fleet-health overview (agent health summary, pending
/// approvals, version histogram) with the full agents management grid below it — the former
/// standalone Agents tab was folded in here. The agents grid keeps its filter/sort/pagination and
/// htmx fragment refresh (?fragment=1 returns just the _AgentsTable partial).
/// </summary>
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

    // ── Overview cards ──────────────────────────────────────────────────────────
    public AgentHealthVm? Health { get; private set; }
    public List<AgentListItem> Pending { get; private set; } = [];
    public List<VersionCount> VersionHistogram { get; private set; } = [];
    public string? LoadError { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;

    // ── Agents grid (folded in from the former /fleet/agents page) ────────────────
    public List<AgentListItem> Agents { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public GridState Grid { get; private set; } = null!;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Zone { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Version { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Liveness { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Dir { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        // The agents grid is shared between the full page and the htmx fragment refresh.
        await LoadAgentsGridAsync(ct);
        if (string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial("_AgentsTable", this);
        }

        await LoadOverviewAsync(ct);
        return Page();
    }

    private async Task LoadOverviewAsync(CancellationToken ct)
    {
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

    private async Task LoadAgentsGridAsync(CancellationToken ct)
    {
        string? afterSortKey = null;
        string? afterCreatedAt = null;
        string? afterAgentId = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 3, out string[] parts))
        {
            afterSortKey = parts[0];
            afterCreatedAt = parts[1];
            afterAgentId = parts[2];
        }

        (List<AgentListItem> items, string? next) = await AgentsApi.QueryAsync(
            _db,
            Status,
            Zone,
            Version,
            Liveness,
            Q,
            afterSortKey,
            afterCreatedAt,
            afterAgentId,
            AgentsApi.DefaultLimit,
            ct,
            Sort,
            Dir
        );
        Agents = items;
        NextCursor = next;

        (List<string> zones, List<string> versions) = await AgentsApi.GetFilterFacetsAsync(_db, ct);

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Status))
        {
            activeFilters["status"] = Status;
        }

        if (!string.IsNullOrEmpty(Zone))
        {
            activeFilters["zone"] = Zone;
        }

        if (!string.IsNullOrEmpty(Version))
        {
            activeFilters["version"] = Version;
        }

        if (!string.IsNullOrEmpty(Liveness))
        {
            activeFilters["liveness"] = Liveness;
        }

        GridModel grid = GridModelBuilder.Build(
            "/fleet",
            [
                new FilterSpec(
                    "status",
                    "Status",
                    [
                        new FilterValue("pending", "Pending"),
                        new FilterValue("approved", "Approved"),
                        new FilterValue("disabled", "Disabled"),
                    ]
                ),
                new FilterSpec(
                    "liveness",
                    "Liveness",
                    AgentsApi.LivenessValues.Select(v => new FilterValue(v, v)).ToList()
                ),
                new FilterSpec(
                    "zone",
                    "Zone",
                    zones.Select(z => new FilterValue(z, z)).ToList()
                ),
                new FilterSpec(
                    "version",
                    "Version",
                    versions.Select(v => new FilterValue(v, v)).ToList()
                ),
            ],
            activeFilters,
            Q,
            AgentsApi.DefaultSort,
            Sort,
            Dir,
            AgentsApi.SortableColumns,
            After,
            NextCursor,
            "#agents-table",
            AgentsApi.DefaultDir
        );
        Grid = grid.Grid;
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;
    }
}

internal static partial class OverviewModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Fleet overview page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record VersionCount(string Version, long Count);