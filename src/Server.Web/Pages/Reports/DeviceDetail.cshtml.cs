using JMW.Discovery.Core.Analysis;

using JMW.Discovery.Server;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.ManualFacts;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class DeviceDetailModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<DeviceDetailModel> _logger;
    private readonly NpgsqlDataSource _db;
    private readonly DeviceRegistry _deviceRegistry;

    public DeviceDetailModel(
        IAntiforgery antiforgery,
        NpgsqlDataSource db,
        DeviceRegistry deviceRegistry,
        ILogger<DeviceDetailModel> logger
    )
    {
        _antiforgery = antiforgery;
        _db = db;
        _deviceRegistry = deviceRegistry;
        _logger = logger;
    }

    public string AntiforgeryToken { get; private set; } = string.Empty;

    private const int HistoryLimit = 50;

    private static readonly Dictionary<string, (string Label, string Href, string NavKey)> KnownOrigins =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["devices"] = ("Devices", "/devices", "devices"),
            ["subnets"] = ("Subnets", "/subnets", "subnets"),
            ["agents"] = ("Agents", "/fleet/agents", "agents"),
            ["arp"] = ("ARP Table", "/arp", "arp"),
            ["services"] = ("Services", "/services", "services"),
            ["dashboard"] = ("Dashboard", "/dashboard", "dashboard"),
            ["changes"] = ("Change Feed", "/changes", "changes"),
            ["ports"] = ("Open Ports", "/ports", "ports"),
            ["containers"] = ("Containers", "/containers", "containers"),
        };

    public bool Found { get; private set; }
    public string DeviceId { get; private set; } = string.Empty;
    public string ManagementStatus { get; private set; } = string.Empty;
    public string BreadcrumbLabel { get; private set; } = "Devices";
    public string BreadcrumbHref { get; private set; } = "/devices";
    public string ActiveNavKey { get; private set; } = "devices";
    public string? Hostname { get; private set; }
    public string? FriendlyName { get; private set; }
    public string? Vendor { get; private set; }
    public string? VendorSourceName { get; private set; }
    public string? Kind { get; private set; }
    public string? LastSeenIp { get; private set; }
    public string? OsFamily { get; private set; }
    public string? OsDistro { get; private set; }
    public DateTime? LastSeen { get; private set; }

    public Section<IReadOnlyList<DeviceFingerprint>> Sources { get; } = new([]);
    public Section<IReadOnlyList<DeviceServiceRow>> Services { get; } = new([]);
    public Section<SystemFacts?> SystemFacts { get; } = new(null);
    public Section<HardwareFacts?> HardwareFacts { get; } = new(null);
    public Section<IReadOnlyList<ComponentRow>> Components { get; } = new([]);
    public Section<IReadOnlyList<SightingRow>> Sightings { get; } = new([]);
    public Section<IReadOnlyList<string>> AdvertisedServices { get; } = new([]);
    public Section<IReadOnlyList<FactRow>> AllFacts { get; } = new([]);
    public Section<IReadOnlyList<RenderedFactView>> FactViews { get; } = new([]);
    public Section<IReadOnlyList<HistoryRow>> History { get; } = new([]);
    public Section<ThroughputHistory> Throughput { get; } = new(new ThroughputHistory(null, []));

    /// <summary>Overridable catalog offered by the fact-path combo box (docs/plans/architecture-operator-facts.md).</summary>
    public IReadOnlyList<string> OverridablePaths => OperatorFactCatalog.OverridablePaths;

    /// <summary>Every operator-authored (override or arbitrary) fact currently set for this device.</summary>
    public IReadOnlyList<OperatorFactRow> OperatorFacts { get; private set; } = [];

    /// <summary>Grouped, data-only section nav for the detail page (built after all sections load).</summary>
    public IReadOnlyList<DeviceSectionGroup> NavGroups { get; private set; } = [];

    /// <summary>
    /// Section the page opens on: History when there's an open incident (the most
    /// judgment-relevant fact on the page), else Hardware for managed / Discovery Sources for
    /// discovered, falling back to the first populated section. Overridden by a valid <c>?tab=</c>
    /// in the browser.
    /// </summary>
    public string DefaultSection { get; private set; } = "allfacts";

    /// <summary>Open (unresolved) incidents from History — the page's single "is something
    /// currently wrong" signal, surfaced in the identity header and the History nav badge.</summary>
    public int OpenIncidentCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, string? from, CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        (BreadcrumbLabel, BreadcrumbHref, ActiveNavKey) =
            from is not null && KnownOrigins.TryGetValue(from, out (string Label, string Href, string NavKey) origin)
                ? origin
                : ResolveOriginFromReferer(Request.Headers.Referer.ToString());

        if (!Guid.TryParse(id, out Guid deviceId))
        {
            return NotFound();
        }

        // A merged-away device id has no devices row anymore (see DeviceRegistry.MergeLosersAsync)
        // — redirect stale bookmarks/links to the survivor instead of 404ing or rendering nothing.
        string resolvedId = await _deviceRegistry.ResolveAliasAsync(deviceId.ToString(), ct);
        if (!resolvedId.Equals(deviceId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(Url.Content($"~/devices/{resolvedId}") + Request.QueryString);
        }

        DeviceId = deviceId.ToString();
        string deviceKey = DeviceId;

        (Guid DeviceId, string ManagementStatus, string? Hostname, string? FriendlyName, string? OsFamily, string?
            OsDistro,
            DateTimeOffset? LastSeen, string? Vendor, string?
            VendorSourceName,
            string? Kind, string? CpuModel, long? CpuCores, long? TotalMemBytes, string? SystemVendor, string?
            SystemModel, string? SystemSerial, string? LastSeenIp)
            summary;
        await using (NpgsqlConnection summaryConn = await _db.OpenConnectionAsync(ct))
        {
            summary = await summaryConn.GetDeviceSummaryAsync(deviceId, ct).FirstOrDefaultAsync(ct);
        }

        if (summary == default)
        {
            return NotFound();
        }

        Found = true;
        ManagementStatus = summary.ManagementStatus;
        Hostname = summary.Hostname;
        FriendlyName = summary.FriendlyName;
        Vendor = summary.Vendor;
        VendorSourceName = summary.VendorSourceName;
        Kind = summary.Kind;
        LastSeenIp = summary.LastSeenIp;
        OsFamily = summary.OsFamily;
        OsDistro = summary.OsDistro;
        LastSeen = summary.LastSeen?.UtcDateTime;

        await Task.WhenAll(
            LoadAsync<IReadOnlyList<HistoryRow>>(
                _logger,
                History,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.ListEntityHistoryAsync("device", deviceKey, HistoryLimit, ct)
                        .Select(r => new HistoryRow(r.Kind ?? "unknown", r.TypeName ?? "unknown", r.Detail, r.At ?? DateTimeOffset.UtcNow, r.Duration, r.Resolution))
                        .ToListAsync(ct);
                }
            ),
            LoadAsync(
                _logger,
                Throughput,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    List<ThroughputPoint> points = [];
                    string? interfaceName = null;
                    await foreach ((long? bytes, DateTimeOffset? collectedAt, string? ifaceName) in
                        c.GetDeviceInterfaceThroughputHistoryAsync(deviceKey, ct))
                    {
                        interfaceName = ifaceName;
                        if (bytes.HasValue && collectedAt.HasValue)
                        {
                            points.Add(new ThroughputPoint(bytes.Value, collectedAt.Value.UtcDateTime));
                        }
                    }

                    return new ThroughputHistory(interfaceName, points);
                }
            ),
            LoadAsync<IReadOnlyList<DeviceFingerprint>>(
                _logger,
                Sources,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceFingerprintsAsync(deviceId, ct)
                        .Select(f => new DeviceFingerprint(f.FpType, f.FpValue, f.Source, f.LastSeen.UtcDateTime))
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<DeviceServiceRow>>(
                _logger,
                Services,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.ListDeviceServicesAsync(deviceKey, ct)
                        .Select(s => new DeviceServiceRow(
                                s.Service,
                                s.Type,
                                s.CaStatus,
                                s.RootNotAfter?.UtcDateTime,
                                s.TotalQueries,
                                s.BlockedPct
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync(
                _logger,
                SystemFacts,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    (string? Hostname, string? OsFamily, string? OsDistro, DateTimeOffset UpdatedAt) row =
                        await c.GetDeviceSystemAsync(deviceKey, ct).FirstOrDefaultAsync(ct);
                    return row == default
                        ? null
                        : new SystemFacts(
                            row.Hostname,
                            row.OsFamily,
                            row.OsDistro,
                            row.UpdatedAt.UtcDateTime
                        );
                }
            ),
            LoadAsync(
                _logger,
                HardwareFacts,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    (string? CpuModel, string? CpuVendor, long? CpuCores, long? CpuLogicalCores, double? CpuMhz, long?
                        TotalMemBytes, string? SystemVendor, string? SystemModel, string? SystemSerial, string?
                        BiosVersion, string? Virtualization, DateTimeOffset UpdatedAt) row =
                            await c.GetDeviceHardwareAsync(deviceKey, ct).FirstOrDefaultAsync(ct);
                    return row == default
                        ? null
                        : new HardwareFacts(
                            row.CpuModel,
                            row.CpuVendor,
                            row.CpuCores,
                            row.CpuLogicalCores,
                            row.CpuMhz,
                            row.TotalMemBytes,
                            row.SystemVendor,
                            row.SystemModel,
                            row.SystemSerial,
                            row.BiosVersion,
                            row.Virtualization,
                            row.UpdatedAt.UtcDateTime
                        );
                }
            ),
            LoadAsync<IReadOnlyList<ComponentRow>>(
                _logger,
                Components,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceComponentsAsync(deviceKey, ct)
                        .Select(r => new ComponentRow(
                                r.HwComponent,
                                r.Class,
                                r.Slot,
                                r.Description,
                                r.Vendor,
                                r.Model,
                                r.Serial,
                                r.Firmware,
                                r.Status,
                                r.IsFru
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<SightingRow>>(
                _logger,
                Sightings,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceSightingsAsync(deviceId, ct)
                        .Select(r => new SightingRow(
                                r.ObserverId,
                                r.ObserverHostname,
                                r.Ip,
                                r.Sources,
                                r.Oui,
                                r.OuiCountry,
                                r.Services
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<string>>(
                _logger,
                AdvertisedServices,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceAdvertisedServicesAsync(deviceId, ct)
                        .Select(r => r.Service)
                        .Where(s => s.Length > 0)
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<FactRow>>(
                _logger,
                AllFacts,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceAllFactsAsync(deviceId, ct)
                        .Select(r => new FactRow(
                                r.AttributePath ?? "",
                                FactViewRenderer.ExtractRowKey(r.KeyValues, "Device"),
                                r.Value,
                                r.Origin,
                                r.SourceName,
                                (r.CollectedAt ?? DateTimeOffset.MinValue).UtcDateTime
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync(
                _logger,
                FactViews,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    List<FactViewFact> facts = await c.GetDeviceAllFactsAsync(deviceId, ct)
                        .Select(r => new FactViewFact(
                                r.AttributePath ?? "",
                                FactViewRenderer.ExtractRowKey(r.KeyValues, "Device"),
                                r.Value
                            )
                        )
                        .ToListAsync(ct);

                    // Pre-resolve OUI for every distinct interface MAC (or obscured-MAC OUI
                    // prefix) the "Interfaces" view's OUI Computed column could ask for —
                    // oui_vendor/oui_country are Postgres functions, not facts, so they can't be
                    // read off the fact list like everything else this renders.
                    HashSet<string> ouiKeys = facts
                        .Where(f => f.AttributePath == FactPaths.InterfaceMAC && f.Value is not null)
                        .Select(f => f.Value!)
                        .Concat(
                            facts.Where(f => f.AttributePath == FactPaths.InterfaceObscuredMAC && f.Value is not null)
                                .Select(f => FactViewLibrary.ObscuredMacOuiPrefix(f.Value))
                                .Where(v => v is not null)
                                .Select(v => v!)
                        )
                        .ToHashSet(StringComparer.Ordinal);

                    Dictionary<string, (string? Vendor, string? Country)> ouiByKey = new(StringComparer.Ordinal);
                    foreach (string key in ouiKeys)
                    {
                        ouiByKey[key] = await c.ResolveOuiAsync(key, ct).FirstOrDefaultAsync(ct);
                    }

                    FactViewRenderContext ctx = new(mac => mac is not null && ouiByKey.TryGetValue(mac, out (string?, string?) v)
                        ? v
                        : (null, null));
                    return FactViewRenderer.Render(facts, FactViewLibrary.All, ctx);
                }
            )
        );

        if (User.IsInRole("admin"))
        {
            await using NpgsqlConnection operatorConn = await _db.OpenConnectionAsync(ct);
            OperatorFacts = await operatorConn.GetDeviceOperatorFactsAsync(deviceId, ct)
                .Select(r => new OperatorFactRow(
                        r.AttributePath,
                        FormatScope(r.KeyValues),
                        ExtractKeys(r.KeyValues),
                        OperatorFactCatalog.IsOverride(r.AttributePath) ? "Override" : "Arbitrary",
                        r.Label,
                        r.Value,
                        r.SourceName,
                        r.CollectedAt.UtcDateTime
                    )
                )
                .ToListAsync(ct);
        }

        OpenIncidentCount = History.Data.Count(h => h.Kind == "open");
        BuildNav();

        return Page();
    }

    /// <summary>
    /// Compact display of an operator fact's non-device scope from its key_values JSON: "Device" for
    /// a device-only fact, else the child-collection keys joined as "Interface[aa:bb:...]".
    /// </summary>
    private static string FormatScope(string? keyValuesJson)
    {
        if (string.IsNullOrEmpty(keyValuesJson))
        {
            return "Device";
        }

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(keyValuesJson);
        List<string> parts = [];
        foreach (System.Text.Json.JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "Device", StringComparison.Ordinal))
            {
                parts.Add($"{prop.Name}[{prop.Value.GetString()}]");
            }
        }

        return parts.Count == 0 ? "Device" : string.Join(".", parts);
    }

    /// <summary>The non-device key values (path order) of an operator fact — the array the revert
    /// call needs alongside the attribute-path template.</summary>
    private static string[] ExtractKeys(string? keyValuesJson)
    {
        if (string.IsNullOrEmpty(keyValuesJson))
        {
            return [];
        }

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(keyValuesJson);
        List<string> keys = [];
        foreach (System.Text.Json.JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "Device", StringComparison.Ordinal))
            {
                keys.Add(prop.Value.GetString() ?? string.Empty);
            }
        }

        return [.. keys];
    }

    /// <summary>The overridable catalog serialized for the fact-path combo box (read client-side).</summary>
    public string CatalogJson => System.Text.Json.JsonSerializer.Serialize(OperatorFactCatalog.OverridablePaths);

    /// <summary>
    /// Assembles the grouped section nav from the built-in sections and the rendered fact views,
    /// keeping only sections that actually have data. Within a group, the curated built-in
    /// sections come first (in the order below), then the fact-view long tail in library order.
    /// A multi-row section carries a count for its nav chip; single-record sheets carry none.
    /// </summary>
    private void BuildNav()
    {
        // Built-in sections: (id, label, group, hasData, count?). Count is null for single-record
        // sheets (Hardware, BACnet) where a chip would be meaningless. There's no standalone
        // "System" section — Hostname/OS are already in the identity header, so that used to be
        // a near-duplicate tab; its one bit of unique info (fact freshness) moved to the OS row.
        (string Id, string Label, FactViewGroup Group, bool Show, int? Count)[] builtins =
        [
            ("summary", "Summary", FactViewGroup.Summary, true, null),
            ("history", "History", FactViewGroup.History, true, History.Data.Count),
            ("hardware", "Hardware", FactViewGroup.Hardware, HardwareFacts.Data is not null, null),
            ("components", "Components", FactViewGroup.Hardware, Components.Data.Count > 0, Components.Data.Count),
            ("advertised", "Advertised Services", FactViewGroup.Network, AdvertisedServices.Data.Count > 0,
                AdvertisedServices.Data.Count),
            ("sightings", "Seen By", FactViewGroup.Network, Sightings.Data.Count > 0, Sightings.Data.Count),
            ("sources", "Discovery Sources", FactViewGroup.Discovery, Sources.Data.Count > 0, Sources.Data.Count),
            ("services", "Services", FactViewGroup.Discovery, Services.Data.Count > 0, Services.Data.Count),
            ("allfacts", "All Facts", FactViewGroup.Discovery, true, AllFacts.Data.Count),
            ("operator-facts", "Operator Facts", FactViewGroup.Custom, User.IsInRole("admin"), null),
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
                    items.Add(new DeviceSectionItem(id, itemLabel, count, id == "history" && OpenIncidentCount > 0));
                }
            }

            foreach (RenderedFactView view in FactViews.Data)
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

        // An open incident is the most judgment-relevant thing on the page, so it wins the
        // landing slot outright. Otherwise the new Summary dashboard is the landing page.
        string preferred = OpenIncidentCount > 0 ? "history" : "summary";
        DefaultSection = flatIds.Contains(preferred) ? preferred
            : flatIds.Count > 0 ? flatIds[0]
            : "allfacts";
    }

    /// <summary>
    /// Stable DOM id for a fact-view section, e.g. "Hardware Details" → "fv-hardware-details".
    /// Used by both the nav builder and the panel markup so their data-tab / data-panel ids match.
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

    /// <summary>Row count of a rendered fact view by title (0 if absent) — for at-a-glance stats
    /// on sections now served from facts rather than a projection.</summary>
    public int FactViewCount(string title) =>
        FactViews.Data.FirstOrDefault(v => v.Title == title)?.Rows.Count ?? 0;

    /// <summary>
    /// Builds an SVG path's "d" attribute for the interface-throughput sparkline. Interface[].
    /// TotalBytes is a raw cumulative counter, not a rate, so this derives bytes/sec from
    /// consecutive samples first (clamping a negative delta — an interface/counter reset — to
    /// zero rather than showing a spurious drop), then plots those rate samples. Y is scaled to
    /// the observed peak (there's no natural fixed bound like a percentage has); <paramref
    /// name="peakBytesPerSec" /> is returned so the caller can label it. Null when there are
    /// fewer than two raw samples — a rate needs at least one interval.
    /// </summary>
    public static string? BuildThroughputSparklinePath(
        IReadOnlyList<ThroughputPoint> points,
        double width,
        double height,
        out double peakBytesPerSec
    )
    {
        peakBytesPerSec = 0;
        if (points.Count < 2)
        {
            return null;
        }

        List<(DateTime At, double BytesPerSec)> rates = new(points.Count - 1);
        for (int i = 1; i < points.Count; i++)
        {
            double deltaSeconds = (points[i].At - points[i - 1].At).TotalSeconds;
            if (deltaSeconds <= 0)
            {
                continue;
            }

            long deltaBytes = Math.Max(0, points[i].Bytes - points[i - 1].Bytes);
            rates.Add((points[i].At, deltaBytes / deltaSeconds));
        }

        if (rates.Count == 0)
        {
            return null;
        }

        peakBytesPerSec = rates.Max(r => r.BytesPerSec);
        double yMax = peakBytesPerSec > 0 ? peakBytesPerSec : 1;
        DateTime start = rates[0].At;
        double spanSeconds = (rates[^1].At - start).TotalSeconds;

        double X(DateTime t) => spanSeconds <= 0 ? 0 : (t - start).TotalSeconds / spanSeconds * width;
        double Y(double bytesPerSec) => height - Math.Clamp(bytesPerSec / yMax, 0, 1) * height;

        System.Text.StringBuilder sb = new();
        sb.Append("M ").Append(X(rates[0].At).ToString("F1")).Append(' ').Append(Y(rates[0].BytesPerSec).ToString("F1"));
        for (int i = 1; i < rates.Count; i++)
        {
            sb.Append(" L ").Append(X(rates[i].At).ToString("F1")).Append(' ').Append(Y(rates[i].BytesPerSec).ToString("F1"));
        }

        return sb.ToString();
    }

    private static async Task LoadAsync<T>(ILogger logger, Section<T> section, Func<Task<T>> load)
    {
        try
        {
            section.Data = await load();
        }
        catch (NpgsqlException ex)
        {
            section.Error = ex.Message;
            DeviceDetailModelLog.SectionLoadFailed(logger, ex);
        }
    }

    // Fallback for links that predate the ?from= convention (or arrive from outside the app).
    private static (string Label, string Href, string NavKey) ResolveOriginFromReferer(string? referer)
    {
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out Uri? uri))
        {
            string path = uri.AbsolutePath;
            if (path.StartsWith("/subnets", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["subnets"];
            }

            if (path.StartsWith("/fleet/agents", StringComparison.OrdinalIgnoreCase)
             || path.StartsWith("/admin/agents", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["agents"];
            }

            if (path.StartsWith("/arp", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["arp"];
            }

            if (path.StartsWith("/services", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["services"];
            }

            if (path.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["dashboard"];
            }

            if (path.StartsWith("/changes", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["changes"];
            }

            if (path.StartsWith("/ports", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["ports"];
            }

            if (path.StartsWith("/containers", StringComparison.OrdinalIgnoreCase))
            {
                return KnownOrigins["containers"];
            }
        }

        return KnownOrigins["devices"];
    }
}

internal static partial class DeviceDetailModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Device detail section load failed.")]
    public static partial void SectionLoadFailed(ILogger logger, Exception ex);
}

public sealed class Section<T>
{
    public T Data { get; set; }
    public string? Error { get; set; }

    public Section(T initial)
    {
        Data = initial;
    }
}

public sealed record SystemFacts(
    string? Hostname,
    string? OsFamily,
    string? OsDistro,
    DateTime UpdatedAt
);

public sealed record HardwareFacts(
    string? CpuModel,
    string? CpuVendor,
    long? CpuCores,
    long? CpuLogicalCores,
    double? CpuMhz,
    long? TotalMemBytes,
    string? SystemVendor,
    string? SystemModel,
    string? SystemSerial,
    string? BiosVersion,
    string? Virtualization,
    DateTime UpdatedAt
);

public sealed record DeviceServiceRow(
    string Service,
    string? Type,
    string? CaStatus,
    DateTime? RootNotAfter,
    long? TotalQueries,
    double? BlockedPct
);

/// <summary>One row in the device's History tab — an incident (open/resolved) or a one-shot
/// change event, narrated via IncidentDisplay.Label.</summary>
public sealed record HistoryRow(string Kind, string TypeName, string? Detail, DateTimeOffset At, TimeSpan? Duration,
    string? Resolution);

/// <summary>One raw cumulative Interface[].TotalBytes sample.</summary>
public sealed record ThroughputPoint(long Bytes, DateTime At);

/// <summary>The busiest interface's throughput history — see
/// GetDeviceInterfaceThroughputHistoryAsync. InterfaceName is null when there's no metric data
/// for this device yet.</summary>
public sealed record ThroughputHistory(string? InterfaceName, IReadOnlyList<ThroughputPoint> Points);

public sealed record ComponentRow(
    string HwComponent,
    string? Class,
    string? Slot,
    string? Description,
    string? Vendor,
    string? Model,
    string? Serial,
    string? Firmware,
    string? Status,
    bool? IsFru
);

/// <summary>One operator-authored fact (override or arbitrary) for the Operator Facts tab.</summary>
public sealed record OperatorFactRow(
    string AttributePath,
    string Scope,
    string[] Keys,
    string Kind,
    string? Label,
    string? Value,
    string? SetBy,
    DateTime? SetAt
);

/// <summary>One current fact known about a device (for the All Facts tab).</summary>
public sealed record FactRow(
    string AttributePath,
    string Key,
    string? Value,
    string? Origin,
    string? SourceName,
    DateTime CollectedAt
);

/// <summary>A labelled group of section-nav items in the device detail sidebar.</summary>
public sealed record DeviceSectionGroup(string Label, IReadOnlyList<DeviceSectionItem> Items);

/// <summary>
/// One clickable section in the device detail nav. <paramref name="Count" /> is the
/// row count for multi-row sections (rendered as a chip) and null for single-record sheets.
/// <paramref name="Attention" /> flags the chip as needing attention (e.g. History with an
/// open incident) so it renders in the crit color instead of the neutral default.
/// </summary>
public sealed record DeviceSectionItem(string Id, string Label, int? Count, bool Attention = false);

/// <summary>One observer's view of this device (an observer↔neighbor sighting).</summary>
public sealed record SightingRow(
    string ObserverId,
    string? ObserverHostname,
    string? Ip,
    string? Sources,
    string? Oui,
    string? OuiCountry,
    string? Services
);