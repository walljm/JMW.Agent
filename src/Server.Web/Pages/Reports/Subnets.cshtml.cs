using System.Text.Json;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

/// <summary>
/// Lists L3 subnets synthesized at query time by <see cref="SubnetsApi" /> — not a paginated
/// fleet-scale report like Arp/Ports/Containers, so it skips the keyset-cursor machinery: subnet
/// count is bounded by the network's actual topology, not by fleet size. Also carries the L2
/// (physical/port-adjacency) graph from <see cref="L2TopologyApi" /> — see docs/plans/d3-l2-l3.md.
/// Both graphs render via the shared D3 renderer (wwwroot/js/topology-graph.js), fed as JSON.
/// </summary>
[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class SubnetsModel : PageModel
{
    private static readonly JsonSerializerOptions GraphJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ILogger<SubnetsModel> _logger;
    private readonly NpgsqlDataSource _db;

    public SubnetsModel(NpgsqlDataSource db, ILogger<SubnetsModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<SubnetListItem> Subnets { get; private set; } = [];

    /// <summary>
    /// L3 subnet/router graph (see <see cref="SubnetsApi.GetGraphAsync" />), serialized as JSON
    /// for the D3 renderer — always the complete graph, not filtered by <see cref="Q" />; the
    /// table below is the filtered drill-down view.
    /// </summary>
    public string L3GraphJson { get; private set; } = "{}";

    /// <summary>L2 device/port adjacency graph (see <see cref="L2TopologyApi.GetGraphAsync" />).</summary>
    public string L2GraphJson { get; private set; } = "{}";

    public string? LoadError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    /// <summary>Normalizes the bound `?tab=` query value; unrecognized values default to "topology".</summary>
    public string ActiveTab => Tab switch
    {
        "list" => "list",
        "l2" => "l2",
        _ => "topology",
    };

    public string ClearHref => ActiveTab == "topology" ? "/subnets" : $"/subnets?tab={ActiveTab}";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Subnets = await SubnetsApi.QueryAsync(_db, Q, ct);

            SubnetGraph l3Graph = await SubnetsApi.GetGraphAsync(_db, ct);
            L3GraphJson = JsonSerializer.Serialize(
                new { nodes = l3Graph.Nodes, edges = l3Graph.Edges },
                GraphJsonOptions
            );

            L2Graph l2Graph = await L2TopologyApi.GetGraphAsync(_db, ct);
            L2GraphJson = JsonSerializer.Serialize(
                new { nodes = l2Graph.Nodes, edges = l2Graph.Edges },
                GraphJsonOptions
            );
        }
        catch (NpgsqlException ex)
        {
            LoadError = "This section could not be loaded. Try refreshing in a moment.";
            SubnetsModelLog.LoadFailed(_logger, ex);
        }

        return Page();
    }
}

internal static partial class SubnetsModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Subnets page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}