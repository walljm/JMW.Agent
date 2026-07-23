using System.Globalization;

namespace JMW.Discovery.Server.Pages.Reports;

/// <summary>A labelled count used by breakdown bars (services-by-type, composition).</summary>
public sealed record LabelCount(string Label, long Count);

/// <summary>Network zone: device/service/zone totals + 24h coverage split.</summary>
public sealed record TotalsVm(
    long TotalDevices,
    long ManagedDevices,
    long DiscoveredDevices,
    long ServicesTotal,
    IReadOnlyList<LabelCount> ServicesByType,
    long Reporting24h,
    long Quiet24h,
    long DistinctZones,
    IReadOnlyList<string> ZoneNames
);

/// <summary>Recent-activity headline counts.</summary>
public sealed record ActivityCountsVm(long NewDevices7d, long NotSeen7d, long Changes24h);

public sealed record NewDeviceRow(Guid DeviceId, string Display, string ManagementStatus, DateTimeOffset CreatedAt);

public sealed record NotSeenRow(Guid DeviceId, string Display, DateTimeOffset? LastSeen);

/// <summary>
/// One Recent Activity row — an incident (open or resolved, with duration) or a one-shot change
/// event, narrated via IncidentDisplay.Label. Replaces the old raw fact-diff ChangeRow.
/// </summary>
public sealed record ActivityRow(
    string Kind, // 'open' | 'resolved' | 'event'
    string TypeName,
    string EntityKind,
    string EntityId,
    string? Detail,
    DateTimeOffset At,
    TimeSpan? Duration,
    string? EntityDisplay
);

/// <summary>Fleet health: agent approval counts + heartbeat-derived liveness + a worst-first list.</summary>
public sealed record AgentHealthVm(
    long Total,
    long Approved,
    long Pending,
    long Online,
    long Stale,
    long Offline,
    IReadOnlyList<AgentRow> Agents
);

public sealed record AgentRow(
    Guid AgentId,
    string Hostname,
    string Status,
    DateTimeOffset? LastHeartbeat,
    string? Zone,
    string? Version,
    string? PassiveMode,
    string Liveness
);

/// <summary>
/// Collection pipeline rollup + hourly error sparkline geometry, plus the fleet-wide daily
/// facts-sent/confirmed-changes trend (two series, see GetCollectionDailyFactsSent.sql /
/// GetCollectionDailyChanges.sql).
/// </summary>
public sealed record CollectionVm(
    long FactsSent,
    long AgentsWithErrors,
    long AvgDurationMs,
    long AgentsReporting,
    string SparkPoints,
    long ErrorPeak,
    bool HasErrors,
    string DailySentPoints,
    string DailySentAreaPath,
    string DailyChangesPoints,
    long DailySentAvg,
    long DailyChangesAvg
);

/// <summary>One open-incident-type row for the Needs Attention panel.</summary>
public sealed record IncidentCountRow(string IncidentType, long OpenCount, long DistinctEntities);

/// <summary>
/// Needs-Attention posture rollup — one query over open incidents (see IncidentQueries.
/// GetOpenIncidentCountsAsync), grouped by type; a new incident type shows up here with no new
/// SQL. cert_expiring (CertExpiringSweepService) is the most recent signal migrated onto this —
/// see IncidentTypeRegistry's remarks on why it needed periodic reconciliation, not an
/// ingest-triggered evaluator, to get here.
/// </summary>
public sealed record PostureVm(IReadOnlyList<IncidentCountRow> IncidentCounts)
{
    /// <summary>True when nothing needs attention — drives the healthy "all clear" empty state.</summary>
    public bool AllClear => IncidentCounts.Count == 0;
}

/// <summary>Network composition breakdowns (top-N + "Other" already rolled up).</summary>
public sealed record CompositionVm(
    IReadOnlyList<LabelCount> ByVendor,
    IReadOnlyList<LabelCount> ByOsFamily,
    IReadOnlyList<LabelCount> ByKind,
    IReadOnlyList<LabelCount> ByManagementStatus,
    IReadOnlyList<LabelCount> ByDiscoverySource
);

/// <summary>Small pure helpers for dashboard rendering (top-N rollup, inline-SVG sparklines).</summary>
public static class DashboardViz
{
    /// <summary>
    /// Rolls a descending list down to <paramref name="n" /> rows, summing the remainder into a
    /// trailing "Other" row. Empty/whitespace labels become <paramref name="unknownLabel" />.
    /// </summary>
    public static IReadOnlyList<LabelCount> TopNWithOther(
        IEnumerable<LabelCount> rows,
        int n,
        string unknownLabel = "Unknown"
    )
    {
        List<LabelCount> normalized = rows
            .Select(r => new LabelCount(string.IsNullOrWhiteSpace(r.Label) ? unknownLabel : r.Label, r.Count))
            .ToList();

        if (normalized.Count <= n)
        {
            return normalized;
        }

        List<LabelCount> top = normalized.Take(n).ToList();
        long other = normalized.Skip(n).Sum(r => r.Count);
        if (other > 0)
        {
            top.Add(new LabelCount("Other", other));
        }

        return top;
    }

    /// <summary>
    /// Builds an SVG polyline points string for a sparkline over a 0..width × 0..height box
    /// (y inverted so larger values sit higher). Returns an empty string for &lt; 2 points.
    /// </summary>
    public static string SparkPoints(IReadOnlyList<long> values, double width = 100, double height = 36) =>
        SparkPoints(values, values.Count == 0 ? 0 : values.Max(), width, height);

    /// <summary>
    /// Same as <see cref="SparkPoints(IReadOnlyList{long}, double, double)" /> but scaled against
    /// an explicit <paramref name="max" /> rather than this series' own max — use this to plot two
    /// series on one shared y-scale so their relative magnitude stays comparable.
    /// </summary>
    public static string SparkPoints(IReadOnlyList<long> values, long max, double width = 100, double height = 36)
    {
        if (values.Count < 2)
        {
            return string.Empty;
        }

        double range = max <= 0 ? 1 : max;
        double stepX = width / (values.Count - 1);

        return string.Join(
            " ",
            values.Select((v, i) =>
                {
                    double x = i * stepX;
                    double y = height - (v / range * (height - 2)) - 1;
                    return string.Create(CultureInfo.InvariantCulture, $"{x:0.#},{y:0.#}");
                }
            )
        );
    }

    /// <summary>Closed area path under the sparkline polyline, for a subtle fill.</summary>
    public static string AreaPath(IReadOnlyList<long> values, double width = 100, double height = 36) =>
        AreaPath(values, values.Count == 0 ? 0 : values.Max(), width, height);

    /// <summary>Same as <see cref="AreaPath(IReadOnlyList{long}, double, double)" /> but scaled against an explicit <paramref name="max" />.</summary>
    public static string AreaPath(IReadOnlyList<long> values, long max, double width = 100, double height = 36)
    {
        string points = SparkPoints(values, max, width, height);
        if (points.Length == 0)
        {
            return string.Empty;
        }

        string[] pts = points.Split(' ');
        string first = pts[0];
        string last = pts[^1];
        double firstX = double.Parse(first.Split(',')[0], CultureInfo.InvariantCulture);
        double lastX = double.Parse(last.Split(',')[0], CultureInfo.InvariantCulture);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"M{first} L{string.Join(" L", pts[1..])} L{lastX:0.#},{height} L{firstX:0.#},{height} Z"
        );
    }
}