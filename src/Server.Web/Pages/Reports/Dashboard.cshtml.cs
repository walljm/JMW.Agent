using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

/// <summary>
/// Fleet Dashboard (SCR-003). Renders grouped, attention-first panels. Each panel is an
/// independent htmx fragment (<c>?fragment=&lt;name&gt;</c>) that refreshes on its own cadence and
/// fails in isolation — a query error in one panel sets that panel's error message and leaves
/// the rest of the page rendering. A full page request loads every panel.
/// </summary>
[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class DashboardModel : PageModel
{
    // Recency / trend windows and list caps (kept here so partials and loaders agree).
    public const int NewDeviceDays = 7;
    public const int NotSeenDays = 7;
    public const int CollectionTrendDays = 14;
    private const int NewDeviceCap = 5;
    private const int NotSeenCap = 5;
    private const int ChangesCap = 8;
    private const int AgentCap = 8;
    private const int CompositionTop = 5;

    private static readonly TimeSpan FastTtl = TimeSpan.FromSeconds(30); // recent activity
    private static readonly TimeSpan MedTtl = TimeSpan.FromSeconds(60); // attention + fleet health
    private static readonly TimeSpan SlowTtl = TimeSpan.FromSeconds(300); // network shape

    private readonly NpgsqlDataSource _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DashboardModel> _logger;

    public DashboardModel(NpgsqlDataSource db, IMemoryCache cache, ILogger<DashboardModel> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    // Panel view models (null until loaded / on error).
    public PostureVm? Posture { get; private set; }
    public AgentHealthVm? AgentHealth { get; private set; }
    public CollectionVm? Collection { get; private set; }
    public ActivityCountsVm? ActivityCounts { get; private set; }
    public IReadOnlyList<NotSeenRow> NotSeen { get; private set; } = [];
    public IReadOnlyList<NewDeviceRow> NewDevices { get; private set; } = [];
    public IReadOnlyList<ActivityRow> Changes { get; private set; } = [];
    public TotalsVm? Totals { get; private set; }
    public CompositionVm? Composition { get; private set; }

    /// <summary>Per-panel load errors, keyed by fragment name; rendered as an isolated section error.</summary>
    public Dictionary<string, string> PanelErrors { get; } = new(StringComparer.Ordinal);

    public string? PanelError(string panel) => PanelErrors.TryGetValue(panel, out string? e) ? e : null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        string fragment = Request.Query["fragment"].ToString();

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        switch (fragment)
        {
            case "attention":
                await LoadPosture(conn, ct);
                return Partial("_AttentionPanel", this);
            case "agents":
                await LoadAgents(conn, ct);
                return Partial("_AgentsPanel", this);
            case "collection":
                await LoadCollection(conn, ct);
                return Partial("_CollectionPanel", this);
            case "not-seen":
                await LoadActivityCounts(conn, ct);
                await LoadNotSeen(conn, ct);
                return Partial("_NotSeenPanel", this);
            case "new-devices":
                await LoadActivityCounts(conn, ct);
                await LoadNewDevices(conn, ct);
                return Partial("_NewDevicesPanel", this);
            case "changes":
                await LoadActivityCounts(conn, ct);
                await LoadChanges(conn, ct);
                return Partial("_ChangesPanel", this);
            case "totals":
                await LoadTotals(conn, ct);
                return Partial("_TotalsPanel", this);
            case "composition":
                await LoadComposition(conn, ct);
                return Partial("_CompositionPanel", this);
            default:
                // Full page: load every panel (independently, so one failure is contained).
                await LoadPosture(conn, ct);
                await LoadAgents(conn, ct);
                await LoadCollection(conn, ct);
                await LoadActivityCounts(conn, ct);
                await LoadNotSeen(conn, ct);
                await LoadNewDevices(conn, ct);
                await LoadChanges(conn, ct);
                await LoadTotals(conn, ct);
                await LoadComposition(conn, ct);
                return Page();
        }
    }

    // ── Panel loaders (cache-first; NpgsqlException → per-panel error, others bubble) ─────

    private async Task LoadPosture(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_posture", out PostureVm? cached))
        {
            Posture = cached;
            return;
        }

        try
        {
            List<IncidentCountRow> counts = [];
            await foreach ((string incidentType, long? openCount, long? distinctEntities) in
                conn.GetOpenIncidentCountsAsync(ct))
            {
                counts.Add(new IncidentCountRow(incidentType, openCount ?? 0, distinctEntities ?? 0));
            }

            CertsExpiringResult certs = await conn.GetCertsExpiringAsync(ct).FirstOrDefaultAsync(ct);
            PostureVm vm = new(counts, certs.CertsExpiring ?? 0);
            Cache("dash_posture", vm, MedTtl);
            Posture = vm;
        }
        catch (NpgsqlException ex) { Fail("attention", ex); }
    }

    private async Task LoadAgents(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_agents", out AgentHealthVm? cached))
        {
            AgentHealth = cached;
            return;
        }

        try
        {
            (long? Total, long? Approved, long? Pending, long? Online, long? Stale, long? Offline) s =
                await conn.GetAgentHealthSummaryAsync(ct).FirstOrDefaultAsync(ct);
            List<AgentRow> agents = [];
            await foreach ((Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone,
                string? Version, string? PassiveDiscoveryMode, string? Liveness) a in
                conn.GetAgentHealthListAsync(AgentCap, ct))
            {
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

            AgentHealthVm vm = new(
                s.Total ?? 0,
                s.Approved ?? 0,
                s.Pending ?? 0,
                s.Online ?? 0,
                s.Stale ?? 0,
                s.Offline ?? 0,
                agents
            );
            Cache("dash_agents", vm, MedTtl);
            AgentHealth = vm;
        }
        catch (NpgsqlException ex) { Fail("agents", ex); }
    }

    private async Task LoadCollection(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_collection", out CollectionVm? cached))
        {
            Collection = cached;
            return;
        }

        try
        {
            (long? FactsSentTotal, long? AgentsWithErrors, long? AvgDurationMs, long? AgentsReporting) s =
                await conn.GetCollectionSummaryAsync(ct).FirstOrDefaultAsync(ct);

            Dictionary<DateTime, long> byHour = [];
            await foreach ((DateTimeOffset? Bucket, long? Errors) r in
                conn.GetCollectionErrorSeriesAsync(ct))
            {
                if (r.Bucket.HasValue) { byHour[r.Bucket.Value.UtcDateTime] = r.Errors ?? 0; }
            }

            List<long> series = FillHourly(byHour, 24);
            long peak = series.Count > 0 ? series.Max() : 0;

            Dictionary<DateTime, long> sentByDay = [];
            await foreach ((DateTimeOffset? Day, long? FactsSent) r in
                conn.GetCollectionDailyFactsSentAsync(CollectionTrendDays, ct))
            {
                if (r.Day.HasValue) { sentByDay[r.Day.Value.UtcDateTime.Date] = r.FactsSent ?? 0; }
            }

            Dictionary<DateTime, long> changesByDay = [];
            await foreach ((DateTimeOffset? Day, long? Count) r in
                conn.GetCollectionDailyChangesAsync(CollectionTrendDays, ct))
            {
                if (r.Day.HasValue) { changesByDay[r.Day.Value.UtcDateTime.Date] = r.Count ?? 0; }
            }

            List<long> dailySent = FillDaily(sentByDay, CollectionTrendDays);
            List<long> dailyChanges = FillDaily(changesByDay, CollectionTrendDays);
            long dailyMax = Math.Max(
                dailySent.Count > 0 ? dailySent.Max() : 0,
                dailyChanges.Count > 0 ? dailyChanges.Max() : 0
            );

            CollectionVm vm = new(
                s.FactsSentTotal ?? 0,
                s.AgentsWithErrors ?? 0,
                s.AvgDurationMs ?? 0,
                s.AgentsReporting ?? 0,
                DashboardViz.SparkPoints(series),
                peak,
                peak > 0,
                DashboardViz.SparkPoints(dailySent, dailyMax),
                DashboardViz.AreaPath(dailySent, dailyMax),
                DashboardViz.SparkPoints(dailyChanges, dailyMax),
                dailySent.Count > 0 ? (long)Math.Round(dailySent.Average()) : 0,
                dailyChanges.Count > 0 ? (long)Math.Round(dailyChanges.Average()) : 0
            );
            Cache("dash_collection", vm, MedTtl);
            Collection = vm;
        }
        catch (NpgsqlException ex) { Fail("collection", ex); }
    }

    private async Task LoadActivityCounts(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_activity", out ActivityCountsVm? cached))
        {
            ActivityCounts = cached;
            return;
        }

        try
        {
            (long? NewDevices7d, long? NotSeen7d, long? Changes24h) s =
                await conn.GetActivitySummaryAsync(ct).FirstOrDefaultAsync(ct);
            ActivityCountsVm vm = new(s.NewDevices7d ?? 0, s.NotSeen7d ?? 0, s.Changes24h ?? 0);
            Cache("dash_activity", vm, FastTtl);
            ActivityCounts = vm;
        }
        catch (NpgsqlException ex) { Fail("activity", ex); }
    }

    private async Task LoadNotSeen(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_not_seen", out List<NotSeenRow>? cached))
        {
            NotSeen = cached!;
            return;
        }

        try
        {
            List<NotSeenRow> rows = [];
            await foreach ((Guid DeviceId, string? FriendlyName, DateTimeOffset? LastSeen) r in
                conn.GetNotSeenDevicesAsync(NotSeenDays, NotSeenCap, ct))
            {
                rows.Add(new NotSeenRow(r.DeviceId, Display(r.FriendlyName), r.LastSeen));
            }

            Cache("dash_not_seen", rows, FastTtl);
            NotSeen = rows;
        }
        catch (NpgsqlException ex) { Fail("not-seen", ex); }
    }

    private async Task LoadNewDevices(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_new_devices", out List<NewDeviceRow>? cached))
        {
            NewDevices = cached!;
            return;
        }

        try
        {
            List<NewDeviceRow> rows = [];
            await foreach ((Guid DeviceId, string? FriendlyName, string ManagementStatus, DateTimeOffset CreatedAt) r
                in conn.GetNewDevicesAsync(NewDeviceDays, NewDeviceCap, ct))
            {
                rows.Add(
                    new NewDeviceRow(r.DeviceId, Display(r.FriendlyName), r.ManagementStatus, r.CreatedAt)
                );
            }

            Cache("dash_new_devices", rows, FastTtl);
            NewDevices = rows;
        }
        catch (NpgsqlException ex) { Fail("new-devices", ex); }
    }

    private async Task LoadChanges(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_changes", out List<ActivityRow>? cached))
        {
            Changes = cached!;
            return;
        }

        try
        {
            List<ActivityRow> rows = [];
            await foreach ((string? Kind, string? TypeName, string? EntityKind, string? EntityId, string? Detail,
                DateTimeOffset? At, TimeSpan? Duration, string? Resolution, string? EntityName) r in
                conn.ListRecentActivityAsync(ChangesCap, ct))
            {
                rows.Add(new ActivityRow(r.Kind ?? "unknown", r.TypeName ?? "unknown", r.EntityKind ?? "unknown", r.EntityId ?? "unknown", r.Detail, r.At ?? DateTimeOffset.UtcNow, r.Duration, r.EntityName));
            }

            Cache("dash_changes", rows, FastTtl);
            Changes = rows;
        }
        catch (NpgsqlException ex) { Fail("changes", ex); }
    }

    private async Task LoadTotals(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_totals", out TotalsVm? cached))
        {
            Totals = cached;
            return;
        }

        try
        {
            (long? TotalDevices, long? ManagedDevices, long? DiscoveredDevices, long? ServicesTotal,
                long? DistinctZones, string? ZoneNames, long? Reporting24h, long? Quiet24h) n =
                    await conn.GetNetworkSummaryAsync(ct).FirstOrDefaultAsync(ct);

            List<LabelCount> svc = [];
            await foreach ((string Type, long? Count) r in conn.GetServicesByTypeAsync(ct))
            {
                svc.Add(new LabelCount(r.Type, r.Count ?? 0));
            }

            List<string> zones = string.IsNullOrWhiteSpace(n.ZoneNames)
                ? []
                : n.ZoneNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            TotalsVm vm = new(
                n.TotalDevices ?? 0,
                n.ManagedDevices ?? 0,
                n.DiscoveredDevices ?? 0,
                n.ServicesTotal ?? 0,
                DashboardViz.TopNWithOther(svc, CompositionTop),
                n.Reporting24h ?? 0,
                n.Quiet24h ?? 0,
                n.DistinctZones ?? 0,
                zones
            );
            Cache("dash_totals", vm, SlowTtl);
            Totals = vm;
        }
        catch (NpgsqlException ex) { Fail("totals", ex); }
    }

    private async Task LoadComposition(NpgsqlConnection conn, CancellationToken ct)
    {
        if (TryCache("dash_composition", out CompositionVm? cached))
        {
            Composition = cached;
            return;
        }

        try
        {
            List<LabelCount> mgmt = await ReadLabelCounts(conn.GetCompositionByManagementStatusAsync(ct), ct);
            CompositionVm vm = new(
                DashboardViz.TopNWithOther(
                    await ReadLabelCounts(conn.GetCompositionByVendorAsync(ct), ct),
                    CompositionTop
                ),
                DashboardViz.TopNWithOther(
                    await ReadLabelCounts(conn.GetCompositionByOsFamilyAsync(ct), ct),
                    CompositionTop
                ),
                DashboardViz.TopNWithOther(
                    await ReadLabelCounts(conn.GetCompositionByKindAsync(ct), ct),
                    CompositionTop
                ),
                // Only two buckets (managed/discovered); no roll-up needed. Proper-case for display —
                // the stored values are lowercase, but we don't show raw canonical tokens to users.
                mgmt.Select(r => r with { Label = ProperCaseStatus(r.Label) }).ToList(),
                DashboardViz.TopNWithOther(
                    await ReadLabelCounts(conn.GetCompositionByDiscoverySourceAsync(ct), ct),
                    CompositionTop
                )
            );
            Cache("dash_composition", vm, SlowTtl);
            Composition = vm;
        }
        catch (NpgsqlException ex) { Fail("composition", ex); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<List<LabelCount>> ReadLabelCounts(
        IAsyncEnumerable<(string? Label, long? Count)> rows,
        CancellationToken ct
    )
    {
        List<LabelCount> list = [];
        await foreach ((string? Label, long? Count) r in rows.WithCancellation(ct))
        {
            list.Add(new LabelCount(r.Label ?? "Unknown", r.Count ?? 0));
        }

        return list;
    }

    /// <summary>Fills a value per hour for the last <paramref name="count" /> hours (oldest→newest), zero where absent.</summary>
    private static List<long> FillHourly(Dictionary<DateTime, long> byHour, int count)
    {
        DateTime now = DateTime.UtcNow;
        DateTime hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        List<long> series = new(count);
        for (int i = count - 1; i >= 0; i--)
        {
            series.Add(byHour.TryGetValue(hour.AddHours(-i), out long v) ? v : 0);
        }

        return series;
    }

    /// <summary>Fills a value per day for the last <paramref name="count" /> days (oldest→newest), zero where absent.</summary>
    private static List<long> FillDaily(Dictionary<DateTime, long> byDay, int count)
    {
        DateTime today = DateTime.UtcNow.Date;
        List<long> series = new(count);
        for (int i = count - 1; i >= 0; i--)
        {
            series.Add(byDay.TryGetValue(today.AddDays(-i), out long v) ? v : 0);
        }

        return series;
    }

    private static string Display(string? hostname) =>
        string.IsNullOrWhiteSpace(hostname) ? "—" : hostname;

    /// <summary>Proper-cases a management-status token ("managed" → "Managed") for display.</summary>
    private static string ProperCaseStatus(string status) =>
        string.IsNullOrEmpty(status) ? status : char.ToUpperInvariant(status[0]) + status[1..];

    private bool TryCache<T>(string key, out T? value) where T : class =>
        _cache.TryGetValue(key, out value) && value is not null;

    private void Cache<T>(string key, T value, TimeSpan ttl) where T : class =>
        _cache.Set(key, value, ttl);

    private void Fail(string panel, NpgsqlException ex)
    {
        // Generic user-facing text only — the raw DB error (table/column/constraint names) stays in
        // the log, never in the response, per the layer-separation rule.
        PanelErrors[panel] = "This section could not be loaded. Try refreshing in a moment.";
        DashboardModelLog.PanelLoadFailed(_logger, panel, ex);
    }
}

internal static partial class DashboardModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Dashboard panel '{panel}' load failed.")]
    public static partial void PanelLoadFailed(ILogger logger, string panel, Exception ex);
}