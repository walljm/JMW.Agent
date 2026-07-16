using System.Text.Json;

using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Agents;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Fleet;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class AgentDetailModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;
    private readonly ReleaseManager _releases;

    public AgentDetailModel(IAntiforgery antiforgery, NpgsqlDataSource db, ReleaseManager releases)
    {
        _antiforgery = antiforgery;
        _db = db;
        _releases = releases;
    }

    // Fallback collector names, used only when an agent hasn't reported its own capabilities
    // yet (older build, or no heartbeat since upgrading) — see BuildCollectorRows/
    // ExtractCapabilities, which prefer the agent-reported list from the capabilities column.
    public static readonly string[] KnownCollectors =
    [
        "OsCollector", "HardwareCollector", "NetworkCollector", "DiskCollector",
        "FilesystemCollector", "ProcessCollector", "PortCollector", "ServiceCollector",
        "DockerCollector", "SecurityCollector", "BatteryCollector", "HwInventoryCollector",
        "UserCollector", "UpdatesCollector", "RouteCollector", "ArpCollector",
        "CertScanCollector", "StepClientCollector", "StepCaCollector",
        "RebootHistoryCollector", "PackageCollector", "GpuCollector", "DhcpLeaseCollector",
        "NetworkDiscoveryCollector",
    ];

    // Network scanners executed by NetworkDiscoveryCollector.
    // Must match the class names registered via WithNetworkScanner() in Program.cs.
    public static readonly string[] KnownScanners =
    [
        "ArpScanner", "MdnsScanner", "SsdpScanner", "SnmpBroadcastScanner",
        "GatewaySnmpArpScanner", "NbnsScanner", "LlmnrScanner", "WsDiscoveryScanner",
        "DnsPtrScanner", "HttpBannerScanner", "TlsCertScanner", "Smb2Scanner",
        "SshBannerScanner", "LdapScanner", "EurekaScanner", "IppScanner",
        "SnmpPrinterScanner", "RokuScanner", "AirPlayScanner", "PingSweepScanner",
        "CoApScanner", "RtspScanner", "MqttScanner", "PhilipsHueScanner",
        "OnvifScanner", "BacnetScanner", "ModbusScanner",
    ];

    // Collector/scanner classes report health under their own runtime `Name` (a short slug,
    // e.g. OsCollector -> "os"), not their class name — collectors_config/capabilities (and
    // therefore KnownCollectors/KnownScanners above) key everything by class name instead.
    // This maps class name -> runtime slug so the Health cue on the Host Collectors/Discovery
    // tabs can look up GetCollectorHealthSummary rows, which are keyed by the runtime slug.
    // Must match each class's `Name` property in the agent assembly.
    public static readonly Dictionary<string, string> CollectorStatNames = new(StringComparer.Ordinal)
    {
        ["OsCollector"] = "os",
        ["HardwareCollector"] = "hardware",
        ["NetworkCollector"] = "network",
        ["DiskCollector"] = "disk",
        ["FilesystemCollector"] = "filesystem",
        ["ProcessCollector"] = "process",
        ["PortCollector"] = "port",
        ["ServiceCollector"] = "service",
        ["DockerCollector"] = "docker",
        ["SecurityCollector"] = "security",
        ["BatteryCollector"] = "battery",
        ["HwInventoryCollector"] = "hw-inventory",
        ["UserCollector"] = "user",
        ["UpdatesCollector"] = "updates",
        ["RouteCollector"] = "routes",
        ["ArpCollector"] = "arp",
        ["CertScanCollector"] = "cert-scan",
        ["StepClientCollector"] = "step-client",
        ["StepCaCollector"] = "step-ca",
        ["RebootHistoryCollector"] = "reboot-history",
        ["PackageCollector"] = "packages",
        ["GpuCollector"] = "gpu",
        ["DhcpLeaseCollector"] = "dhcp-leases",
        ["NetworkDiscoveryCollector"] = "network-discovery",
    };

    public static readonly Dictionary<string, string> ScannerStatNames = new(StringComparer.Ordinal)
    {
        ["ArpScanner"] = "arp",
        ["MdnsScanner"] = "mdns",
        ["SsdpScanner"] = "ssdp",
        ["SnmpBroadcastScanner"] = "snmp-broadcast",
        ["GatewaySnmpArpScanner"] = "gateway-arp",
        ["NbnsScanner"] = "nbns",
        ["LlmnrScanner"] = "llmnr",
        ["WsDiscoveryScanner"] = "ws-discovery",
        ["DnsPtrScanner"] = "dns-ptr",
        ["HttpBannerScanner"] = "http-banner",
        ["TlsCertScanner"] = "tls-cert",
        ["Smb2Scanner"] = "smb2",
        ["SshBannerScanner"] = "ssh-banner",
        ["LdapScanner"] = "ldap",
        ["EurekaScanner"] = "eureka",
        ["IppScanner"] = "ipp",
        ["SnmpPrinterScanner"] = "snmp-printer",
        ["RokuScanner"] = "roku",
        ["AirPlayScanner"] = "airplay",
        ["PingSweepScanner"] = "ping-sweep",
        ["CoApScanner"] = "coap",
        ["RtspScanner"] = "rtsp",
        ["MqttScanner"] = "mqtt",
        ["PhilipsHueScanner"] = "philips-hue",
        ["OnvifScanner"] = "onvif",
        ["BacnetScanner"] = "bacnet",
        ["ModbusScanner"] = "modbus",
    };

    // Reverse lookups (runtime slug -> class name) for translating GetCollectorHealthSummary
    // rows back onto the class-name-keyed InlineHealth dictionary used by the Configuration tabs.
    private static readonly Dictionary<string, string> CollectorClassNameByStatName =
        CollectorStatNames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ScannerClassNameByStatName =
        ScannerStatNames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public bool Found { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;

    public Guid AgentId { get; private set; }
    public string Hostname { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public DateTime? LastHeartbeat { get; private set; }
    public string Liveness { get; private set; } = "offline";
    public string LivenessExplanation { get; private set; } = string.Empty;
    public string? Zone { get; private set; }
    public string? Version { get; private set; }
    public string? Os { get; private set; }
    public string? Arch { get; private set; }
    public string? IpAddress { get; private set; }
    public Guid? DeviceId { get; private set; }

    // Latest signed release published for this agent's platform, if any is newer
    // than what it's currently running. Null when up to date, unknown, or auto-update
    // isn't configured (JMW_RELEASES_DIR unset).
    public string? AvailableUpdateVersion { get; private set; }

    public int HeartbeatIntervalSecs { get; private set; }
    public int DiscoveryIntervalSecs { get; private set; }
    public int InventoryIntervalSecs { get; private set; }

    public List<CollectorRow> Collectors { get; private set; } = [];
    public List<ScannerRow> Scanners { get; private set; } = [];
    public List<TargetRow> Targets { get; private set; } = [];
    public List<CredentialOption> CredentialOptions { get; private set; } = [];
    public List<CycleRow> Cycles { get; private set; } = [];
    public List<string> KnownZones { get; private set; } = [];

    // ── Overview health tiles + inline config cues ──────────────────────────────
    // These use a fixed rolling window (LOOKBACK_HOURS), independent of the Activity date
    // filter, so the Overview stays stable while an operator filters the timeline below it.
    public const int LookbackHours = 24;

    // Collection tile: the most recent cycle (any age) and windowed cycle counts.
    public DateTime? LastCycleAt { get; private set; }
    public int LastCycleFacts { get; private set; }
    public int LastCycleErrors { get; private set; }
    public int LastCycleDurationMs { get; private set; }
    public int WindowCycleTotal { get; private set; }
    public int WindowCycleErrored { get; private set; }

    // Sparkline of facts-per-cycle over recent real collection cycles (oldest → newest);
    // Errored marks bars red. Bare heartbeat ticks are excluded at the query level (they
    // send no facts, so they aren't collection activity).
    public List<int> SparkFacts { get; private set; } = [];
    public List<bool> SparkErrored { get; private set; } = [];

    // Scale for the sparkline bars: the 90th percentile of SparkFacts, not the true max.
    // A periodic full-inventory resync can send 10-1000x the facts of a steady-state cycle;
    // scaling against that true max flattens every normal cycle to a sliver. Capping at the
    // 90th percentile keeps normal variation readable and lets rare bursts clip at 100%
    // instead of dominating the axis.
    public int SparkMax { get; private set; } = 1;

    // Per-collector/scanner health over the fixed window, keyed by name, for the inline
    // status cue on each Configuration row.
    public Dictionary<string, CollectorHealthRow> InlineHealth { get; private set; } = new(StringComparer.Ordinal);

    // Per-remote-target health over the same window, keyed by (target, collector_type) —
    // target is the endpoint for device-scanner stats and label-or-endpoint for service
    // stats, so the Targets tab looks a row up by endpoint first, then label.
    public Dictionary<(string Target, string CollectorType), CollectorHealthRow> TargetHealth { get; private set; } =
        new();

    // Global liveness thresholds (read-only display in Settings — no per-agent editor).
    public int LivenessOnlineMultiplier { get; private set; }
    public int LivenessOfflineCeilingSecs { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? ActivitySince { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ActivityUntil { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ActivityErrorsOnly { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone, string? Version
          , string? Os, string? Arch, string? IpAddress, Guid? DeviceId, int HeartbeatIntervalSecs, int
            DiscoveryIntervalSecs, int InventoryIntervalSecs, JsonElement CollectorsConfig, string? Liveness,
            JsonElement? Capabilities)>
            detailRows = await conn.GetAgentDetailAsync(id, ct).ToListAsync(ct);
        if (detailRows.Count == 0)
        {
            Found = false;
            return Page();
        }

        (Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone, string? Version,
            string? Os, string? Arch, string? IpAddress, Guid? DeviceId, int HeartbeatIntervalSecs, int
            DiscoveryIntervalSecs, int InventoryIntervalSecs, JsonElement CollectorsConfig, string? Liveness,
            JsonElement? Capabilities) d = detailRows[0];
        Found = true;
        AgentId = d.AgentId;
        Hostname = d.Hostname;
        Status = d.Status;
        LastHeartbeat = d.LastHeartbeat?.UtcDateTime;
        Zone = d.Zone;
        Version = d.Version;
        Os = d.Os;
        Arch = d.Arch;
        IpAddress = d.IpAddress;
        DeviceId = d.DeviceId;

        (List<string> zones, _) = await AgentsApi.GetFilterFacetsAsync(_db, ct);
        KnownZones = zones;

        // Mirrors HeartbeatEndpoint's offer gate: only surface a version that would
        // actually be offered over heartbeat (signed, and strictly newer).
        if (_releases.Enabled && d.Os is not null && d.Arch is not null
         && _releases.Latest(d.Os, d.Arch) is { } latest
         && !string.IsNullOrEmpty(latest.Signature)
         && (d.Version is null || ReleaseManager.SemverGreater(d.Version, latest.Version)))
        {
            AvailableUpdateVersion = latest.Version;
        }

        HeartbeatIntervalSecs = d.HeartbeatIntervalSecs;
        DiscoveryIntervalSecs = d.DiscoveryIntervalSecs;
        InventoryIntervalSecs = d.InventoryIntervalSecs;

        // Liveness itself is computed by agent_liveness() (see migration
        // 0056_agent_liveness_settings.sql) — the single definition also used by
        // GetAgentHealthList.sql/GetAgentHealthSummary.sql/AgentsApi.QueryAsync. Only the
        // human-readable explanation is built here, from the same configured settings.
        Liveness = d.Liveness ?? "offline";
        (int OnlineMultiplier, int OfflineCeilingSecs) livenessSettings =
            await conn.GetAgentLivenessSettingsAsync(ct).FirstOrDefaultAsync(ct);
        LivenessOnlineMultiplier = livenessSettings.OnlineMultiplier;
        LivenessOfflineCeilingSecs = livenessSettings.OfflineCeilingSecs;
        LivenessExplanation = DescribeLiveness(
            Liveness,
            LastHeartbeat,
            HeartbeatIntervalSecs,
            livenessSettings.OnlineMultiplier,
            livenessSettings.OfflineCeilingSecs
        );

        Collectors = BuildCollectorRows(d.CollectorsConfig, d.Capabilities);
        Scanners = BuildScannerRows(d.CollectorsConfig, d.Capabilities);

        // Credential options for the target form and name resolution.
        List<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)>
            credRows = await conn.ListCredentialsAsync(null, null, null, 200, ct).ToListAsync(ct);
        CredentialOptions = credRows
            .Select(c => new CredentialOption(c.CredentialId.ToString(), c.Name, c.Type))
            .ToList();
        Dictionary<Guid, string> credNames = credRows.ToDictionary(c => c.CredentialId, c => c.Name);

        // Targets (enabled + disabled); credential names resolved from the lookup.
        List<(Guid TargetId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label, bool Enabled)>
            targetRows = await conn.ListAgentTargetsDetailAsync(id, ct).ToListAsync(ct);
        Targets = targetRows.Select(t => new TargetRow(
                    TargetId: t.TargetId.ToString(),
                    Endpoint: t.Endpoint,
                    CollectorType: t.CollectorType,
                    CredentialId: t.CredentialId?.ToString(),
                    CredentialName: t.CredentialId is { } cid && credNames.TryGetValue(cid, out string? n) ? n : null,
                    Label: t.Label,
                    Enabled: t.Enabled
                )
            )
            .ToList();

        // Recent cycle history — date range and errors-only filter via query string.
        DateTimeOffset? sinceUtc = DateTimeOffset.TryParse(ActivitySince, out DateTimeOffset since)
            ? since.ToUniversalTime()
            : null;
        DateTimeOffset? untilUtc = DateTimeOffset.TryParse(ActivityUntil, out DateTimeOffset until)
            ? until.ToUniversalTime().AddDays(1).AddTicks(-1) // inclusive through the end of the selected day
            : null;

        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> cycleRows =
            await conn.ListAgentCyclesAsync(id, sinceUtc, untilUtc, ActivityErrorsOnly, 100, true, ct).ToListAsync(ct);

        Cycles = cycleRows.Select(r => new CycleRow(
                    r.CycleId,
                    r.CycleAt.UtcDateTime,
                    r.DurationMs,
                    r.FactsSent,
                    r.ErrorCount,
                    r.Collectors,
                    r.Scanners,
                    r.DeviceScanners,
                    r.Services
                )
            )
            .ToList();

        // ── Overview tiles + inline cues (fixed window, filter-independent) ───────────
        DateTimeOffset windowStart = DateTimeOffset.UtcNow.AddHours(-LookbackHours);

        (DateTimeOffset? LastCycleAt, int? LastFacts, int? LastErrors, int? LastDurationMs, int? WindowTotal, int?
            WindowErrored) collection =
            await conn.GetAgentCollectionSummaryAsync(id, windowStart, ct).FirstOrDefaultAsync(ct);
        LastCycleAt = collection.LastCycleAt?.UtcDateTime;
        LastCycleFacts = collection.LastFacts ?? 0;
        LastCycleErrors = collection.LastErrors ?? 0;
        LastCycleDurationMs = collection.LastDurationMs ?? 0;
        WindowCycleTotal = collection.WindowTotal ?? 0;
        WindowCycleErrored = collection.WindowErrored ?? 0;

        // Sparkline series — the exact same rows as the Cycle History tab (cycleRows above:
        // same Since/Until/errors-only filter, same 100-row cap, collectionOnly already
        // excludes bare heartbeat ticks), just reversed to oldest → newest for left-to-right
        // rendering. The .spark CSS is fixed-width bars, right-aligned with overflow clipped,
        // so it renders however many of these actually fit the tile's rendered width rather
        // than stretching a fixed count to fill the space.
        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> sparkRows =
            [.. cycleRows];
        sparkRows.Reverse();
        SparkFacts = sparkRows.Select(r => r.FactsSent).ToList();
        SparkErrored = sparkRows.Select(r => r.ErrorCount > 0).ToList();
        SparkMax = Percentile90(SparkFacts);

        // Inline per-collector/scanner health over the same fixed window, re-keyed onto the
        // class name used by collectors_config/capabilities (see CollectorStatNames/
        // ScannerStatNames — GetCollectorHealthSummary reports each collector/scanner's own
        // runtime slug, not its class name).
        List<(string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs)> inlineRows =
            await conn.GetCollectorHealthSummaryAsync(id, windowStart, null, ct).ToListAsync(ct);
        foreach ((string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) h in inlineRows)
        {
            if (h.Name is not { } inlineName)
            {
                continue;
            }

            string? className = h.Kind switch
            {
                "collector" => CollectorClassNameByStatName.GetValueOrDefault(inlineName),
                "scanner" => ScannerClassNameByStatName.GetValueOrDefault(inlineName),
                _ => null,
            };
            if (className is null)
            {
                continue;
            }

            InlineHealth[className] =
                new CollectorHealthRow(inlineName, h.RunCount ?? 0, h.ErrorCount ?? 0, h.MedianDurationMs, inlineName, h.Kind ?? "collector");
        }

        // Per-target health over the same window for the Targets tab's inline cue.
        List<(string? Target, string? CollectorType, int? RunCount, int? ErrorCount, double? MedianDurationMs)>
            targetHealthRows = await conn.GetTargetHealthSummaryAsync(id, windowStart, null, ct).ToListAsync(ct);
        foreach ((string? Target, string? CollectorType, int? RunCount, int? ErrorCount, double? MedianDurationMs) t
                 in targetHealthRows)
        {
            if (t.Target is not { } statTarget || t.CollectorType is not { } statType)
            {
                continue;
            }

            TargetHealth[(statTarget, statType)] =
                new CollectorHealthRow(statTarget, t.RunCount ?? 0, t.ErrorCount ?? 0, t.MedianDurationMs, statTarget, "target");
        }

        return Page();
    }

    /// <summary>Nearest-rank 90th percentile, floored at 1 (never a zero scale denominator).</summary>
    private static int Percentile90(List<int> values)
    {
        if (values.Count == 0)
        {
            return 1;
        }

        int[] sorted = [.. values];
        Array.Sort(sorted);
        int rank = Math.Clamp((int)Math.Ceiling(0.9 * sorted.Length), 1, sorted.Length);
        return Math.Max(1, sorted[rank - 1]);
    }

    /// <summary>Compact "3m" / "2h" / "4d" age string for the Overview tiles.</summary>
    public static string FormatAgeShort(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h";
        }

        return $"{(int)age.TotalDays}d";
    }

    /// <summary>
    /// The liveness value itself comes from agent_liveness() (see migration
    /// 0056_agent_liveness_settings.sql) — the single, configurable definition also used by
    /// GetAgentHealthList.sql/GetAgentHealthSummary.sql/AgentsApi.QueryAsync. This only formats
    /// a human-readable reason using the same configured multiplier/ceiling, without
    /// re-deriving the state.
    /// </summary>
    private static string DescribeLiveness(
        string liveness,
        DateTime? lastHeartbeat,
        int heartbeatIntervalSecs,
        int onlineMultiplier,
        int offlineCeilingSecs
    )
    {
        if (lastHeartbeat is null)
        {
            return "Offline — no heartbeat has been received yet.";
        }

        TimeSpan age = DateTime.UtcNow - lastHeartbeat.Value;
        string ageStr = FormatAge(age);
        TimeSpan onlineThreshold = TimeSpan.FromSeconds(heartbeatIntervalSecs * onlineMultiplier);

        return liveness switch
        {
            "online" =>
                $"Online — last beat {ageStr} ago, within {onlineMultiplier}× the {heartbeatIntervalSecs}s interval ({onlineThreshold.TotalSeconds:F0}s).",
            "stale" =>
                $"Stale — last beat {ageStr} ago, past {onlineMultiplier}× the {heartbeatIntervalSecs}s interval ({onlineThreshold.TotalSeconds:F0}s) but under the {offlineCeilingSecs / 60}-minute ceiling.",
            _ =>
                $"Offline — last beat {ageStr} ago, past the {offlineCeilingSecs / 60}-minute ceiling.",
        };
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m {age.Seconds}s";
        }

        return $"{(int)age.TotalHours}h {age.Minutes}m";
    }

    private static List<CollectorRow> BuildCollectorRows(JsonElement collectorsConfig, JsonElement? capabilities)
    {
        List<(string Name, bool Supported)> known =
            ExtractCapabilities(capabilities, "collectors")
         ?? KnownCollectors.Select(n => (n, true)).ToList();

        List<CollectorRow> rows = new(known.Count);
        foreach ((string name, bool supported) in known)
        {
            // Matches Agent.cs's IsCollectorEnabled default: every collector defaults to
            // enabled except NetworkDiscoveryCollector, which is opt-in per agent.
            bool enabled = name != "NetworkDiscoveryCollector";
            int? intervalSecs = null;

            if (collectorsConfig.ValueKind == JsonValueKind.Object
             && collectorsConfig.TryGetProperty(name, out JsonElement entry)
             && entry.ValueKind == JsonValueKind.Object)
            {
                if (entry.TryGetProperty("enabled", out JsonElement enabledEl)
                 && (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
                {
                    enabled = enabledEl.GetBoolean();
                }

                if (entry.TryGetProperty("interval_secs", out JsonElement intervalEl)
                 && intervalEl.ValueKind == JsonValueKind.Number)
                {
                    intervalSecs = intervalEl.GetInt32();
                }
            }

            rows.Add(new CollectorRow(name, enabled, intervalSecs, supported));
        }

        return rows;
    }

    private static List<ScannerRow> BuildScannerRows(JsonElement collectorsConfig, JsonElement? capabilities)
    {
        List<(string Name, bool Supported)> known =
            ExtractCapabilities(capabilities, "scanners")
         ?? KnownScanners.Select(n => (n, true)).ToList();

        List<ScannerRow> rows = new(known.Count);
        foreach ((string name, bool supported) in known)
        {
            bool enabled = true;
            int? intervalSecs = null;

            if (collectorsConfig.ValueKind == JsonValueKind.Object
             && collectorsConfig.TryGetProperty(name, out JsonElement entry)
             && entry.ValueKind == JsonValueKind.Object)
            {
                if (entry.TryGetProperty("enabled", out JsonElement enabledEl)
                 && (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
                {
                    enabled = enabledEl.GetBoolean();
                }

                if (entry.TryGetProperty("interval_secs", out JsonElement intervalEl)
                 && intervalEl.ValueKind == JsonValueKind.Number)
                {
                    intervalSecs = intervalEl.GetInt32();
                }
            }

            rows.Add(new ScannerRow(name, enabled, intervalSecs, supported, ScannerFamily(name)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the agent-reported (name, supported) pairs for "collectors" or "scanners" out of
    /// the capabilities JSONB column. Returns null when the agent hasn't reported yet (older
    /// build, or no heartbeat since upgrading) — the caller falls back to the hardcoded
    /// KnownCollectors/KnownScanners list, all assumed supported.
    /// </summary>
    private static List<(string Name, bool Supported)>? ExtractCapabilities(JsonElement? capabilities, string key)
    {
        if (capabilities is not { } caps
         || caps.ValueKind != JsonValueKind.Object
         || !caps.TryGetProperty(key, out JsonElement arr)
         || arr.ValueKind != JsonValueKind.Array
         || arr.GetArrayLength() == 0)
        {
            return null;
        }

        List<(string, bool)> result = [];
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
             || !item.TryGetProperty("name", out JsonElement nameEl)
             || nameEl.ValueKind != JsonValueKind.String
             || nameEl.GetString() is not { } name)
            {
                continue;
            }

            bool supported = item.TryGetProperty("supported", out JsonElement supEl)
             && supEl.ValueKind == JsonValueKind.True;
            result.Add((name, supported));
        }

        return result.Count > 0 ? result : null;
    }

    // Groups the Discovery tab's ~27 scanners by protocol family so the list isn't one flat
    // wall — classified by how each scanner reaches devices, not registration order.
    public static readonly Dictionary<string, string> ScannerFamilies = new(StringComparer.Ordinal)
    {
        ["ArpScanner"] = "Broadcast",
        ["SnmpBroadcastScanner"] = "Broadcast",
        ["NbnsScanner"] = "Broadcast",
        ["MdnsScanner"] = "Multicast",
        ["SsdpScanner"] = "Multicast",
        ["LlmnrScanner"] = "Multicast",
        ["WsDiscoveryScanner"] = "Multicast",
        ["GatewaySnmpArpScanner"] = "Unicast probe",
        ["DnsPtrScanner"] = "Unicast probe",
        ["HttpBannerScanner"] = "Unicast probe",
        ["TlsCertScanner"] = "Unicast probe",
        ["Smb2Scanner"] = "Unicast probe",
        ["SshBannerScanner"] = "Unicast probe",
        ["LdapScanner"] = "Unicast probe",
        ["EurekaScanner"] = "Unicast probe",
        ["RokuScanner"] = "Unicast probe",
        ["AirPlayScanner"] = "Unicast probe",
        ["PingSweepScanner"] = "Unicast probe",
        ["IppScanner"] = "Printer",
        ["SnmpPrinterScanner"] = "Printer",
        ["CoApScanner"] = "IoT",
        ["RtspScanner"] = "IoT",
        ["MqttScanner"] = "IoT",
        ["PhilipsHueScanner"] = "IoT",
        ["OnvifScanner"] = "IoT",
        ["BacnetScanner"] = "IoT",
        ["ModbusScanner"] = "IoT",
    };

    private static string ScannerFamily(string name) =>
        ScannerFamilies.TryGetValue(name, out string? family) ? family : "Other";

    /// <summary>Family display order shared with the Discovery Configuration tab.</summary>
    public static readonly string[] ScannerFamilyOrder =
        ["Broadcast", "Multicast", "Unicast probe", "Printer", "IoT", "Other"];

    public sealed record CollectorRow(string Name, bool Enabled, int? IntervalSecs, bool Supported = true);

    public sealed record ScannerRow(
        string Name,
        bool Enabled,
        int? IntervalSecs,
        bool Supported = true,
        string Family = "Other"
    );

    public sealed record TargetRow(
        string TargetId,
        string Endpoint,
        string CollectorType,
        string? CredentialId,
        string? CredentialName,
        string? Label,
        bool Enabled
    );

    public sealed record CredentialOption(string CredentialId, string Name, string Type);

    public sealed record CycleRow(
        long CycleId,
        DateTime CycleAt,
        int DurationMs,
        int FactsSent,
        int ErrorCount,
        JsonElement Collectors,
        JsonElement Scanners,
        JsonElement DeviceScanners,
        JsonElement Services
    );

    // Name is the raw runtime slug (used to query facts-by-source for "Affected facts");
    // DisplayName adds a "(collector)"/"(scanner)" suffix only when Name collides across kinds.
    // Kind is "collector" or "scanner", from GetCollectorHealthSummary's discriminator.
    public sealed record CollectorHealthRow(
        string Name,
        int RunCount,
        int ErrorCount,
        double? MedianDurationMs,
        string DisplayName,
        string Kind
    );
}