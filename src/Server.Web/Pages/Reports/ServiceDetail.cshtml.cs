using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ServiceDetailModel : PageModel
{
    private readonly NpgsqlDataSource _db;

    public ServiceDetailModel(NpgsqlDataSource db)
    {
        _db = db;
    }

    public string Service { get; private set; } = string.Empty;
    public string? ServiceId { get; private set; }
    public string? Type { get; private set; }
    public string? DeviceId { get; private set; }

    public string? CaStatus { get; private set; }
    public string? CaAddress { get; private set; }
    public string? RootSubjectDn { get; private set; }
    public DateTime? RootNotBefore { get; private set; }
    public DateTime? RootNotAfter { get; private set; }
    public string? RootFingerprint { get; private set; }
    public string? IntSubjectDn { get; private set; }
    public DateTime? IntNotBefore { get; private set; }
    public DateTime? IntNotAfter { get; private set; }

    public long? TotalQueries { get; private set; }
    public long? TotalBlocked { get; private set; }
    public double? BlockedPct { get; private set; }

    /// <summary>Blocked-% history over the last 30 days, oldest first — see
    /// ListServiceBlockedPctHistoryAsync. Empty when this isn't a DNS service, or one with no
    /// history yet.</summary>
    public IReadOnlyList<(double Value, DateTime CollectedAt)> BlockedPctTrend { get; private set; } = [];

    public IReadOnlyList<ServiceProvisioner> Provisioners { get; private set; } = [];
    public IReadOnlyList<ServiceZone> Zones { get; private set; } = [];
    public IReadOnlyList<ServiceScope> Scopes { get; private set; } = [];
    public IReadOnlyList<ServiceDnsRecord> Records { get; private set; } = [];
    public IReadOnlyList<RenderedFactView> FactViews { get; private set; } = [];

    /// <summary>Every fact keyed to this service (attribute/key/value) — feeds the "All Facts" section.</summary>
    public IReadOnlyList<FactViewFact> AllFacts { get; private set; } = [];

    /// <summary>Grouped left-nav sections (mirrors the Device Detail layout).</summary>
    public IReadOnlyList<DeviceSectionGroup> NavGroups { get; private set; } = [];

    /// <summary>The section the page lands on when no ?tab= is supplied.</summary>
    public string DefaultSection { get; private set; } = "allfacts";

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        Service = id;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        (string Service, string? ServiceId, string? Type, string? DeviceId, string? CaStatus, string? CaAddress, string?
            RootSubjectDn, DateTimeOffset? RootNotBefore, DateTimeOffset? RootNotAfter, string? RootFingerprint, string?
            IntSubjectDn, DateTimeOffset? IntNotBefore, DateTimeOffset? IntNotAfter, long? TotalQueries, long?
            TotalBlocked, double? BlockedPct) detail = await conn.GetServiceDetailAsync(id, ct).FirstOrDefaultAsync(ct);
        if (detail == default)
        {
            return NotFound();
        }

        ServiceId = detail.ServiceId;
        Type = detail.Type;
        DeviceId = detail.DeviceId;
        CaStatus = detail.CaStatus;
        CaAddress = detail.CaAddress;
        RootSubjectDn = detail.RootSubjectDn;
        RootNotBefore = detail.RootNotBefore?.UtcDateTime;
        RootNotAfter = detail.RootNotAfter?.UtcDateTime;
        RootFingerprint = detail.RootFingerprint;
        IntSubjectDn = detail.IntSubjectDn;
        IntNotBefore = detail.IntNotBefore?.UtcDateTime;
        IntNotAfter = detail.IntNotAfter?.UtcDateTime;
        TotalQueries = detail.TotalQueries;
        TotalBlocked = detail.TotalBlocked;
        BlockedPct = detail.BlockedPct;

        // Each reader is fully materialized before the next query runs.
        Provisioners = await conn.GetServiceProvisionersAsync(id, ct)
            .Select(p => new ServiceProvisioner(p.Provisioner, p.ProvisionerType, p.DefaultDuration))
            .ToListAsync(ct);

        Zones = await conn.GetServiceZonesAsync(id, ct)
            .Select(z => new ServiceZone(z.Zone, z.ZoneType))
            .ToListAsync(ct);

        Scopes = await conn.GetServiceScopesAsync(id, ct)
            .Select(s => new ServiceScope(s.Scope, s.Enabled, s.StartAddress, s.EndAddress, s.SubnetMask, s.Gateway))
            .ToListAsync(ct);

        Records = await conn.GetServiceRecordsAsync(id, ct)
            .Select(r => new ServiceDnsRecord(r.Zone, r.Record, r.Rtype, r.Value, r.Ttl))
            .ToListAsync(ct);

        List<FactViewFact> facts = await conn.GetServiceAllFactsAsync(id, ct)
            .Select(r => new FactViewFact(r.AttributePath, FactViewRenderer.ExtractRowKey(r.KeyValues, "Service"), r.Value))
            .ToListAsync(ct);
        AllFacts = facts;
        FactViews = FactViewRenderer.Render(facts, FactViewLibrary.Service);

        if (TotalQueries.HasValue || BlockedPct.HasValue)
        {
            List<(double Value, DateTime CollectedAt)> trend = [];
            await foreach ((double? value, DateTimeOffset? collectedAt) in
                conn.ListServiceBlockedPctHistoryAsync(id, ct))
            {
                if (value.HasValue && collectedAt.HasValue)
                {
                    trend.Add((value.Value, collectedAt.Value.UtcDateTime));
                }
            }

            BlockedPctTrend = trend;
        }

        BuildNav();
        return Page();
    }

    /// <summary>
    /// Builds an SVG path's "d" attribute for a 0-100% step chart (each value holds steady until
    /// the next point, matching facts_history's dedup-on-write semantics — see
    /// ListServiceBlockedPctHistoryAsync) spanning the last 30 days. Y is fixed to the full 0-100%
    /// range rather than data min/max, since a zoomed axis on a percentage is the classic
    /// misleading-chart mistake. Null when there's nothing to draw.
    /// </summary>
    public static string? BuildSparklinePath(IReadOnlyList<(double Value, DateTime CollectedAt)> points, double width, double height)
    {
        if (points.Count == 0)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        DateTime start = now.AddDays(-30);

        double X(DateTime t)
        {
            double span = (now - start).TotalSeconds;
            double frac = span <= 0 ? 0 : (t - start).TotalSeconds / span;
            return Math.Clamp(frac, 0, 1) * width;
        }

        double Y(double pct) => height - Math.Clamp(pct, 0, 100) / 100.0 * height;

        System.Text.StringBuilder sb = new();
        sb.Append("M ").Append(X(points[0].CollectedAt).ToString("F1")).Append(' ').Append(Y(points[0].Value).ToString("F1"));
        for (int i = 1; i < points.Count; i++)
        {
            // Horizontal segment at the PRIOR value up to this point's time (it held steady until
            // now), then a vertical step to the new value — the step-after shape.
            double xAtChange = X(points[i].CollectedAt);
            sb.Append(" L ").Append(xAtChange.ToString("F1")).Append(' ').Append(Y(points[i - 1].Value).ToString("F1"));
            sb.Append(" L ").Append(xAtChange.ToString("F1")).Append(' ').Append(Y(points[i].Value).ToString("F1"));
        }

        // Hold the last known value out to "now" — it's still in effect, not just true at its
        // own timestamp.
        sb.Append(" L ").Append(X(now).ToString("F1")).Append(' ').Append(Y(points[^1].Value).ToString("F1"));
        return sb.ToString();
    }

    /// <summary>
    /// Assembles the grouped left-nav from the curated built-in sections (CA/DNS/DHCP) and the
    /// rendered fact views, keeping only sections that have data — mirrors DeviceDetailModel.BuildNav.
    /// </summary>
    private void BuildNav()
    {
        bool hasCa = CaStatus is not null || RootSubjectDn is not null;
        bool hasDns = TotalQueries.HasValue || BlockedPct.HasValue || Zones.Count > 0 || Records.Count > 0;
        bool hasDhcp = Scopes.Count > 0;

        (string Id, string Label, FactViewGroup Group, bool Show, int? Count)[] builtins =
        [
            ("summary", "Summary", FactViewGroup.Summary, true, null),
            ("ca", "CA Certificate", FactViewGroup.Security, hasCa, null),
            ("dns", "DNS Zones", FactViewGroup.Network, hasDns, Zones.Count > 0 ? Zones.Count : null),
            ("dhcp", "DHCP Scopes", FactViewGroup.Network, hasDhcp, Scopes.Count),
            ("allfacts", "All Facts", FactViewGroup.Discovery, AllFacts.Count > 0, AllFacts.Count),
        ];

        List<DeviceSectionGroup> groups = new(FactViewGroups.Ordered.Count);
        List<string> flatIds = [];
        foreach (FactViewGroup group in FactViewGroups.Ordered)
        {
            string label = group.DisplayName();
            List<DeviceSectionItem> items = [];
            foreach ((string id, string itemLabel, FactViewGroup g, bool show, int? count) in builtins)
            {
                if (g == group && show)
                {
                    items.Add(new DeviceSectionItem(id, itemLabel, count));
                }
            }

            foreach (RenderedFactView view in FactViews)
            {
                if (view.Group == group)
                {
                    int? count = view.Kind == FactViewKind.List ? view.Rows.Count : null;
                    items.Add(new DeviceSectionItem(FactViewSectionId(view.Title), view.Title, count));
                }
            }

            if (items.Count > 0)
            {
                groups.Add(new DeviceSectionGroup(label, items));
                flatIds.AddRange(items.Select(i => i.Id));
            }
        }

        NavGroups = groups;
        DefaultSection = flatIds.Contains("summary") ? "summary"
            : flatIds.Count > 0 ? flatIds[0]
            : "allfacts";
    }

    /// <summary>
    /// Stable DOM id for a fact-view section, e.g. "Add-ons" → "fv-add-ons". Shared by the nav
    /// builder and the panel markup so their data-tab / data-panel ids match. (Same rule as
    /// DeviceDetailModel.FactViewSectionId.)
    /// </summary>
    public static string FactViewSectionId(string title)
    {
        char[] buf = new char[title.Length];
        for (int i = 0; i < title.Length; i++)
        {
            char c = char.ToLowerInvariant(title[i]);
            buf[i] = char.IsLetterOrDigit(c) ? c : '-';
        }

        return "fv-" + new string(buf);
    }
}