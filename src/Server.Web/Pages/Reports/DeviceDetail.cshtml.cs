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
    public bool VendorIsGuess { get; private set; }
    public string? VendorSourceName { get; private set; }
    public string? Kind { get; private set; }
    public string? LastSeenIp { get; private set; }
    public string? OsFamily { get; private set; }
    public string? OsDistro { get; private set; }
    public bool OsDistroIsGuess { get; private set; }
    public DateTime? LastSeen { get; private set; }

    public Section<IReadOnlyList<DeviceFingerprint>> Sources { get; } = new([]);
    public Section<IReadOnlyList<DeviceServiceRow>> Services { get; } = new([]);
    public Section<SystemFacts?> SystemFacts { get; } = new(null);
    public Section<HardwareFacts?> HardwareFacts { get; } = new(null);
    public Section<IReadOnlyList<InterfaceRow>> Interfaces { get; } = new([]);
    public Section<IReadOnlyList<DiskRow>> Disks { get; } = new([]);
    public Section<IReadOnlyList<FilesystemRow>> Filesystems { get; } = new([]);
    public Section<IReadOnlyList<ContainerRow>> Containers { get; } = new([]);
    public Section<IReadOnlyList<PortRow>> Ports { get; } = new([]);
    public Section<IReadOnlyList<ComponentRow>> Components { get; } = new([]);
    public Section<IReadOnlyList<SightingRow>> Sightings { get; } = new([]);
    public Section<IReadOnlyList<string>> AdvertisedServices { get; } = new([]);
    public Section<IReadOnlyList<FactRow>> AllFacts { get; } = new([]);
    public Section<IReadOnlyList<RenderedFactView>> FactViews { get; } = new([]);
    public Section<IReadOnlyList<HistoryRow>> History { get; } = new([]);

    /// <summary>Existing fact paths an operator may set directly (docs/plans/user-provided.md).</summary>
    public IReadOnlyList<string> EditablePaths => ManualFactCatalog.EditablePaths;

    /// <summary>Custom field definitions (schema), for the per-device value-entry panel.</summary>
    public IReadOnlyList<CustomFieldDefinition> CustomFieldDefinitions { get; private set; } = [];

    /// <summary>This device's current value per custom field slug, derived from AllFacts.</summary>
    public IReadOnlyDictionary<string, string?> CustomFieldValues { get; private set; } =
        new Dictionary<string, string?>();

    /// <summary>Existing-path facts this device currently has a manual (operator-set) value for.</summary>
    public IReadOnlyList<(string Path, string? Value)> ManualOverrides { get; private set; } = [];

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
            string? OsDistroGuess, DateTimeOffset? LastSeen, string? Vendor, string? VendorGuess, string?
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
        Vendor = summary.Vendor ?? summary.VendorGuess;
        VendorIsGuess = summary.Vendor is null && summary.VendorGuess is not null;
        VendorSourceName = summary.VendorSourceName;
        Kind = summary.Kind;
        LastSeenIp = summary.LastSeenIp;
        OsFamily = summary.OsFamily;
        OsDistro = summary.OsDistro ?? summary.OsDistroGuess;
        OsDistroIsGuess = summary.OsDistro is null && summary.OsDistroGuess is not null;
        LastSeen = summary.LastSeen?.UtcDateTime;

        await using (NpgsqlConnection customFieldConn = await _db.OpenConnectionAsync(ct))
        {
            CustomFieldDefinitions = await customFieldConn.ListCustomFieldDefinitionsAsync(ct)
                .Select(r => new CustomFieldDefinition(r.Id, r.Label, r.Slug, r.TargetViewTitle, r.TargetViewGroup,
                        r.IsNewView, r.CreatedAt, r.CreatedBy
                    )
                )
                .ToListAsync(ct);
        }

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
            LoadAsync<IReadOnlyList<InterfaceRow>>(
                _logger,
                Interfaces,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceInterfacesAsync(deviceKey, ct)
                        .Select(r => new InterfaceRow(
                                r.Interface,
                                r.Name,
                                r.MacAddress,
                                r.ObscuredMac,
                                r.Oui,
                                r.OuiCountry,
                                r.Ipv4,
                                r.Ipv6,
                                r.Mtu,
                                r.Up,
                                r.SpeedBps,
                                r.Duplex,
                                r.Type
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<DiskRow>>(
                _logger,
                Disks,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceDisksAsync(deviceKey, ct)
                        .Select(r => new DiskRow(
                                r.Disk,
                                r.Name,
                                r.Model,
                                r.SizeBytes,
                                r.Type,
                                r.SmartHealth,
                                r.SmartTempC
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<FilesystemRow>>(
                _logger,
                Filesystems,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceFilesystemsAsync(deviceKey, ct)
                        .Select(r => new FilesystemRow(
                                r.Filesystem,
                                r.FsType,
                                r.TotalBytes,
                                r.UsedBytes,
                                r.FreeBytes,
                                r.UsedPct
                            )
                        )
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<ContainerRow>>(
                _logger,
                Containers,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDeviceContainersAsync(deviceKey, ct)
                        .Select(r => new ContainerRow(r.Container, r.Name, r.Image, r.State, r.Health, r.RestartCount))
                        .ToListAsync(ct);
                }
            ),
            LoadAsync<IReadOnlyList<PortRow>>(
                _logger,
                Ports,
                async () =>
                {
                    await using NpgsqlConnection c = await _db.OpenConnectionAsync(ct);
                    return await c.GetDevicePortsAsync(deviceKey, ct)
                        .Select(r => new PortRow(r.Protocol, r.Address, r.Port, r.ProcessName, r.Pid))
                        .ToListAsync(ct);
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
                    IReadOnlyList<FactViewDef> views =
                        CustomFieldViewMerger.MergeDefs(FactViewLibrary.All, CustomFieldDefinitions);
                    return CustomFieldViewMerger.FilterBaselineRows(
                        FactViewRenderer.Render(facts, views),
                        CustomFieldDefinitions
                    );
                }
            )
        );

        CustomFieldValues = CustomFieldDefinitions.ToDictionary(
            d => d.Slug,
            d => AllFacts.Data
                .FirstOrDefault(f => f.AttributePath == FactPaths.CustomFieldValue && f.Key == d.Slug)
                ?.Value,
            StringComparer.Ordinal
        );

        ManualOverrides = ManualFactCatalog.EditablePaths
            .Select(path => AllFacts.Data.FirstOrDefault(f => f.AttributePath == path
             && f.SourceName is { Length: > 0 } sourceName
             && sourceName.StartsWith("user:", StringComparison.Ordinal)
            )
            )
            .OfType<FactRow>()
            .Select(row => (row.AttributePath, row.Value))
            .ToList();

        OpenIncidentCount = History.Data.Count(h => h.Kind == "open");
        BuildNav();

        return Page();
    }

    // Group display order + labels for the section nav. Matches FactViewGroup's declared order.
    private static readonly (FactViewGroup Group, string Label)[] GroupOrder =
    [
        (FactViewGroup.History, "History"),
        (FactViewGroup.Hardware, "Hardware"),
        (FactViewGroup.Storage, "Storage"),
        (FactViewGroup.Network, "Network"),
        (FactViewGroup.Software, "Software"),
        (FactViewGroup.Security, "Security"),
        (FactViewGroup.Protocols, "Protocols"),
        (FactViewGroup.Discovery, "Discovery"),
        (FactViewGroup.Custom, "Custom"),
    ];

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
            ("history", "History", FactViewGroup.History, true, History.Data.Count),
            ("hardware", "Hardware", FactViewGroup.Hardware, HardwareFacts.Data is not null, null),
            ("components", "Components", FactViewGroup.Hardware, Components.Data.Count > 0, Components.Data.Count),
            ("disks", "Disks", FactViewGroup.Storage, Disks.Data.Count > 0, Disks.Data.Count),
            ("filesystems", "Filesystems", FactViewGroup.Storage, Filesystems.Data.Count > 0, Filesystems.Data.Count),
            ("interfaces", "Interfaces", FactViewGroup.Network, Interfaces.Data.Count > 0, Interfaces.Data.Count),
            ("ports", "Ports", FactViewGroup.Network, Ports.Data.Count > 0, Ports.Data.Count),
            ("advertised", "Advertised Services", FactViewGroup.Network, AdvertisedServices.Data.Count > 0,
                AdvertisedServices.Data.Count),
            ("sightings", "Seen By", FactViewGroup.Network, Sightings.Data.Count > 0, Sightings.Data.Count),
            ("containers", "Containers", FactViewGroup.Software, Containers.Data.Count > 0, Containers.Data.Count),
            ("sources", "Discovery Sources", FactViewGroup.Discovery, Sources.Data.Count > 0, Sources.Data.Count),
            ("services", "Services", FactViewGroup.Discovery, Services.Data.Count > 0, Services.Data.Count),
            ("allfacts", "All Facts", FactViewGroup.Discovery, true, AllFacts.Data.Count),
            ("manual-overrides", "Manual Overrides", FactViewGroup.Custom, User.IsInRole("admin"), null),
        ];

        List<DeviceSectionGroup> groups = new(GroupOrder.Length);
        List<string> flatIds = [];
        foreach ((FactViewGroup group, string label) in GroupOrder)
        {
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
        // landing slot outright. Otherwise fall back to management status, then the first
        // populated section.
        string preferred = OpenIncidentCount > 0 ? "history"
            : ManagementStatus == "managed" ? "hardware" : "sources";
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

public sealed record InterfaceRow(
    string Interface,
    string? Name,
    string? MacAddress,
    string? ObscuredMac,
    string? Oui,
    string? OuiCountry,
    string? Ipv4,
    string? Ipv6,
    long? Mtu,
    bool? Up,
    long? SpeedBps,
    string? Duplex,
    string? Type
);

public sealed record DiskRow(
    string Disk,
    string? Name,
    string? Model,
    long? SizeBytes,
    string? Type,
    string? SmartHealth,
    double? SmartTempC
);

public sealed record FilesystemRow(
    string Filesystem,
    string? FsType,
    long? TotalBytes,
    long? UsedBytes,
    long? FreeBytes,
    double? UsedPct
);

public sealed record ContainerRow(
    string Container,
    string? Name,
    string? Image,
    string? State,
    string? Health,
    long? RestartCount
);

public sealed record PortRow(
    string? Protocol,
    string? Address,
    int? Port,
    string? ProcessName,
    long? Pid
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