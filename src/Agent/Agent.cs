using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Agent.Collection.Network;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace JMW.Discovery.Agent;

/// <summary>
/// The agent runtime. Handles registration, approval, and the collection loop.
/// Collectors plug in via IDeviceCollector; the agent container manages
/// everything else: identity, fingerprint-based device resolution, delta
/// tracking, and transport. Facts are emitted RAW — normalization and
/// derivation happen server-side at ingest, not on the agent.
/// Construct via AgentBuilder.
/// </summary>
public sealed class Agent
{
    private readonly AgentConfig _config;
    private readonly IReadOnlyList<IDeviceCollector> _collectors;
    private readonly List<ILocalCollector> _localCollectors;
    private readonly IReadOnlyList<IServiceCollector> _serviceCollectors;
    private readonly ITargetSource _targets;
    private readonly IAgentServerClient _server;
    private readonly string _stateDir;
    private readonly Uri _serverUri;

    // Full registered collector/scanner set (before local-collector's IsSupported filter),
    // captured for capability reporting — see BuildCollectorCapabilities/BuildScannerCapabilities.
    private readonly IReadOnlyList<AgentCapability> _collectorCapabilities;
    private readonly IReadOnlyList<AgentCapability> _scannerCapabilities;

    // ── Runtime state (loaded from / persisted to stateDir) ───────────────────
    private string? _agentId;
    private string? _apiKey;
    private DateTimeOffset? _lastTrackersClearedAt;
    private DateTimeOffset? _lastLogsUploadedAt;

    // Serves on-demand log pulls (journalctl on native systemd, else the in-process ring buffer).
    private readonly AgentLogCollector _logCollector = new(AgentLog.Buffer);

    // ── Server-delivered config (from the heartbeat config block) ─────────────
    // Null until the first heartbeat returns a config block. The file-based config
    // remains the fallback whenever these are null/empty.
    private HeartbeatConfig? _serverConfig;

    // Last time each phase ran, for interval gating (MinValue → runs on the first cycle).
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInventory = DateTimeOffset.MinValue;

    // Server-assigned DeviceId for this host, learned from the facts response of a
    // previous cycle. Used to link loopback service targets to the host device.
    private string? _localDeviceId;

    // LocalMachineFingerprints.CollectAsync spawns several external processes (ioreg,
    // system_profiler, PowerShell-WMI) to read values that are stable for the life of the
    // host — cache them instead of recomputing every discovery/inventory tick. Refreshed
    // hourly rather than never, so a hot-plugged NIC's MAC is eventually picked up.
    private IReadOnlyList<Fingerprint>? _cachedFingerprints;
    private DateTimeOffset _fingerprintsCachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan FingerprintCacheLifetime = TimeSpan.FromHours(1);

    private string AgentId
        => _agentId ?? throw new InvalidOperationException("AgentId not initialized. Call RunAsync first.");

    private string ApiKey => _apiKey
     ?? throw new InvalidOperationException("ApiKey not initialized. Agent must be registered and approved.");

    // Delta trackers keyed by fingerprints hash (stable across restarts).
    // Local device uses the hash of LocalMachineFingerprints.Collect().
    private readonly Dictionary<string, CollectorDeltaTracker> _trackers = new();
    private readonly ILogger<Agent> _logger = AgentLog.CreateLogger<Agent>();

    internal Agent(
        AgentConfig config,
        IReadOnlyList<IDeviceCollector> collectors,
        IReadOnlyList<ILocalCollector> localCollectors,
        IReadOnlyList<INetworkScanner> networkScanners,
        IReadOnlyList<IServiceCollector> serviceCollectors,
        ITargetSource targets,
        IAgentServerClient server,
        string stateDir
    )
    {
        _config = config;
        _collectors = collectors;
        _collectorCapabilities = localCollectors
            .Select(c => new AgentCapability(c.GetType().Name, c.IsSupported))
            .ToList();
        _scannerCapabilities = networkScanners
            .Select(s => new AgentCapability(s.GetType().Name, s.IsSupported))
            .ToList();
        _localCollectors = localCollectors.Where(c => c.IsSupported).ToList();
        _serviceCollectors = serviceCollectors;
        _targets = targets;
        _server = server;
        _stateDir = stateDir;
        _serverUri = new Uri(config.ServerUrl);

        // NetworkDiscoveryCollector is constructed here (not in AgentBuilder) so the
        // scanner filter can capture `this.IsCollectorEnabled`, giving it access to
        // the live server config that arrives via heartbeat at runtime.
        if (networkScanners.Count > 0)
        {
            _localCollectors.Add(new NetworkDiscoveryCollector(networkScanners, IsCollectorEnabled));
        }
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full agent lifecycle. Never returns unless ct is cancelled.
    /// 1. Load or generate AgentId
    /// 2. Register with server and wait for user approval
    /// 3. Collect → analyze → delta → ingest, on every interval
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDir);
        LoadState();

        _agentId ??= Guid.NewGuid().ToString("D");
        SaveAgentId();

        await EnsureApprovedAsync(ct);
        await CollectionLoopAsync(ct);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    private async Task EnsureApprovedAsync(CancellationToken ct)
    {
        if (_apiKey is null)
        {
            Guid agentGuid = Guid.Parse(AgentId);
            AgentRegistrationResponse reg = await _server.RegisterAsync(
                new AgentRegistrationRequest(
                    AgentId: agentGuid,
                    Hostname: _config.Name,
                    Version: AgentVersion.Current,
                    Zone: _config.Zone,
                    PassiveDiscoveryMode: PrivilegeDetector.PassiveDiscoveryMode,
                    Os: DetectOs(),
                    Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                    IpAddress: DetectPrimaryIp()
                ),
                ct
            );

            _apiKey = reg.ApiKey;
            SaveApiKey();

            AgentMessages.Registered(_logger, _config.Name, _agentId);
        }

        // Poll heartbeat until approved. 403 = still pending, 200 = approved.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            bool approved = await _server.CheckApprovalAsync(
                AgentId,
                ApiKey,
                new HeartbeatRequest(
                    AgentId: Guid.Parse(AgentId),
                    Version: AgentVersion.Current,
                    PassiveDiscoveryMode: PrivilegeDetector.PassiveDiscoveryMode,
                    Collectors: _collectorCapabilities,
                    Scanners: _scannerCapabilities
                ),
                ct
            );

            if (approved)
            {
                AgentMessages.Approved(_logger);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    // ── Heartbeat ─────────────────────────────────────────────────────────────

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            HeartbeatResponse response = await _server.HeartbeatAsync(
                AgentId,
                ApiKey,
                new HeartbeatRequest(
                    AgentId: Guid.Parse(AgentId),
                    Version: AgentVersion.Current,
                    PassiveDiscoveryMode: PrivilegeDetector.PassiveDiscoveryMode,
                    Collectors: _collectorCapabilities,
                    Scanners: _scannerCapabilities
                ),
                ct
            );

            // Capture the server config block (if any). It is applied by the
            // collection loop on the same cycle and persists until the next heartbeat.
            if (response.Config is { } config)
            {
                _serverConfig = config;
                // Refresh the trusted-CA set so validating collectors can authenticate
                // private-CA HTTPS endpoints. Applied every cycle so CA rotation propagates.
                CaTrust.Update(config.TrustedCaCertificates);
                ApplyClearTrackersRequestIfNeeded(config.ClearTrackersRequestedAt);
                await ApplyLogsRequestIfNeeded(
                    config.LogsRequestedAt,
                    config.LogsRequestedLines,
                    config.LogsRequestedBefore,
                    ct
                );
            }

            if (response.Update is { } update)
            {
                await TryApplyUpdateAsync(update, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Heartbeat failure is non-fatal — continue collection.
            AgentMessages.HeartbeatFailed(_logger, ex);
        }
    }

    private async Task TryApplyUpdateAsync(UpdateInfo update, CancellationToken ct)
    {
        if (update.Version == AgentVersion.Current)
        {
            return;
        }

        AgentMessages.UpdateOffered(_logger, update.Version);

        if (_server is not HttpAgentServerClient http)
        {
            AgentMessages.UpdateNotHttp(_logger);
            return;
        }

        try
        {
            await Updater.ApplyAsync(update, _serverUri, ApiKey, http.HttpClient, ct);
        }
        catch (Exception ex)
        {
            AgentMessages.UpdateFailed(_logger, ex);
        }
    }

    // ── Collection loop ───────────────────────────────────────────────────────

    private async Task CollectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset cycleStart = DateTimeOffset.UtcNow;

            // Heartbeat every heartbeat interval; it also refreshes the interval config below.
            if (cycleStart - _lastHeartbeat >= HeartbeatInterval)
            {
                _lastHeartbeat = cycleStart;
                await SendHeartbeatAsync(ct);
            }

            // Which phases are due this tick (evaluated after the heartbeat may have updated
            // the intervals). Discovery = network scanners; inventory = host + device + service.
            bool runDiscovery = cycleStart - _lastDiscovery >= DiscoveryInterval;
            bool runInventory = cycleStart - _lastInventory >= InventoryInterval;
            if (runDiscovery) { _lastDiscovery = cycleStart; }

            if (runInventory) { _lastInventory = cycleStart; }

            // File-configured targets, plus any server-delivered targets. Partitioned by
            // collector kind: a target whose CollectorType matches a registered
            // IServiceCollector.ServiceType is service-style; everything else (including a
            // null CollectorType, which falls back to trying every device collector in
            // registration order) is device-style.
            IReadOnlyList<Target> fileTargets = await _targets.GetTargetsAsync(ct);
            IReadOnlyList<Target> allTargets = MergeServerTargets(fileTargets);

            HashSet<string> serviceCollectorTypes =
                new(_serviceCollectors.Select(c => c.ServiceType), StringComparer.OrdinalIgnoreCase);
            List<Target> services = allTargets
                .Where(t => t.CollectorType is { } collectorType && serviceCollectorTypes.Contains(collectorType))
                .ToList();
            List<Target> targets = allTargets.Except(services).ToList();

            // Collect all batch elements for this cycle.
            List<FactBatchElement> batchElements = new();

            // Local collectors contribute one batch element for the host machine (host
            // inventory on the inventory cadence; the network-discovery collector on the
            // discovery cadence).
            LocalCollectResult localResult = new(null, [], []);
            if ((runDiscovery || runInventory) && _localCollectors.Count > 0)
            {
                localResult = await CollectLocalAsync(runDiscovery, runInventory, ct);
                if (localResult.Element is not null)
                {
                    batchElements.Add(localResult.Element);
                }
            }

            // Remote device collectors each produce one batch element per device, plus a
            // per-target activity stat for the cycle summary. Inventory cadence.
            List<DeviceScannerStat> deviceScannerStats = [];
            if (runInventory && targets.Count > 0)
            {
                ConcurrentBag<FactBatchElement> remoteElements = new();
                ConcurrentBag<DeviceScannerStat> deviceStats = new();
                ParallelOptions options = new()
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Min(targets.Count, _config.MaxConcurrency),
                };

                await Parallel.ForEachAsync(
                    targets,
                    options,
                    async (target, innerCt) =>
                    {
                        (FactBatchElement? element, DeviceScannerStat stat) =
                            await CollectDeviceWithStatsAsync(target, innerCt);
                        deviceStats.Add(stat);
                        if (element is not null)
                        {
                            remoteElements.Add(element);
                        }
                    }
                );

                batchElements.AddRange(remoteElements);
                deviceScannerStats = [.. deviceStats];
            }

            // Service collectors run against the service-style targets partitioned above.
            // Services use their own identity model (ServiceFingerprint) and are submitted
            // as a separate request per service for now.
            IReadOnlyList<ServiceStat> serviceStats = [];
            if (runInventory && _serviceCollectors.Count > 0 && services.Count > 0)
            {
                serviceStats = await CollectServicesAsync(services, ct);
            }

            // Build cycle summary and post one AgentFactsRequest per cycle.
            TimeSpan cycleElapsed = DateTimeOffset.UtcNow - cycleStart;
            int totalFacts = batchElements.Sum(b => b.Facts.Count);
            AgentCycleSummary cycleSummary = new(
                (int)cycleElapsed.TotalMilliseconds,
                totalFacts,
                localResult.CollectorStats,
                localResult.ScannerStats,
                deviceScannerStats,
                serviceStats
            );

            AgentFactsRequest request = new(
                AgentId: Guid.Parse(AgentId),
                CollectedAt: cycleStart,
                FactBatches: batchElements,
                CycleSummary: cycleSummary
            );

            try
            {
                AgentFactsResponse response = await _server.PostFactsAsync(ApiKey, request, ct);
                if (batchElements.Count > 0)
                {
                    AgentMessages.FactsPosted(_logger, batchElements.Count, totalFacts, response.AcceptedBatches);
                }

                // The local machine element is always first in FactBatches, and the
                // response resolutions are in batch order — capture our DeviceId so
                // loopback service targets can be linked to this host.
                if (localResult.Element is not null && response.ResolvedDevices.Count > 0)
                {
                    _localDeviceId = response.ResolvedDevices[0].DeviceId;
                }

                if (batchElements.Count > 0)
                {
                    PersistAllTrackers();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Rollback all trackers so the next cycle retries.
                if (batchElements.Count > 0)
                {
                    foreach (CollectorDeltaTracker tracker in _trackers.Values)
                    {
                        tracker.Rollback();
                    }

                    // Persist the rollback immediately so a crash between now and the
                    // next successful cycle doesn't leave stale (pre-rollback) state on disk.
                    PersistAllTrackers();
                }

                AgentMessages.FactsPostFailed(_logger, ex);
            }

            TimeSpan elapsed = DateTimeOffset.UtcNow - cycleStart;
            TimeSpan remaining = LoopTick - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, ct);
            }
        }
    }

    // The three phase cadences (from the server config block, else the file interval):
    //   Heartbeat — check in with the server and pull config.
    //   Discovery — run the network scanners (find devices on the subnet).
    //   Inventory — deep-collect known things (host collectors + device/service targets).
    // Discovery/Inventory default to the heartbeat cadence when unset, so an agent with only
    // a heartbeat interval configured behaves exactly as before (everything every cycle).
    private TimeSpan HeartbeatInterval =>
        _serverConfig is { HeartbeatIntervalSecs: > 0 } c
            ? TimeSpan.FromSeconds(c.HeartbeatIntervalSecs)
            : _config.Interval;

    private TimeSpan DiscoveryInterval =>
        _serverConfig is { DiscoveryIntervalSecs: > 0 } c
            ? TimeSpan.FromSeconds(c.DiscoveryIntervalSecs)
            : HeartbeatInterval;

    private TimeSpan InventoryInterval =>
        _serverConfig is { InventoryIntervalSecs: > 0 } c
            ? TimeSpan.FromSeconds(c.InventoryIntervalSecs)
            : HeartbeatInterval;

    // The loop ticks at the fastest cadence so each phase can fire on schedule.
    private TimeSpan LoopTick
    {
        get
        {
            TimeSpan tick = HeartbeatInterval;
            if (DiscoveryInterval < tick) { tick = DiscoveryInterval; }

            if (InventoryInterval < tick) { tick = InventoryInterval; }

            return tick;
        }
    }

    private bool IsCollectorEnabled(string collectorClassName) =>
        IsCollectorEnabledCore(_serverConfig?.Collectors, collectorClassName);

    /// <summary>
    /// Returns true when the named collector is enabled. A collector is enabled unless the
    /// server config explicitly disables it. Absent an explicit setting (no server config,
    /// or the server config has no entry for this collector), every collector defaults to
    /// enabled EXCEPT <see cref="NetworkDiscoveryCollector" /> — subnet-wide scanning is
    /// opt-in per agent (toggle it on via the Admin UI/API) so a freshly registered agent
    /// only reports on itself until someone explicitly asks it to scan its network.
    /// Pulled out of <see cref="IsCollectorEnabled" /> as a pure function so the default can
    /// be unit-tested without standing up a full <see cref="Agent" />.
    /// </summary>
    internal static bool IsCollectorEnabledCore(
        IReadOnlyDictionary<string, CollectorSetting>? collectors,
        string collectorClassName
    )
    {
        if (collectors is not null && collectors.TryGetValue(collectorClassName, out CollectorSetting? setting))
        {
            return setting.Enabled;
        }

        return collectorClassName != nameof(NetworkDiscoveryCollector);
    }

    /// <summary>
    /// Merges server-delivered targets with file-configured targets. Server targets
    /// are appended; file targets remain the fallback when no server config exists.
    /// </summary>
    private IReadOnlyList<Target> MergeServerTargets(IReadOnlyList<Target> fileTargets)
    {
        if (_serverConfig is not { Targets.Count: > 0 } cfg)
        {
            return fileTargets;
        }

        List<Target> merged = new(fileTargets);
        foreach (TargetConfig target in cfg.Targets)
        {
            merged.Add(
                new Target
                {
                    Endpoint = target.Endpoint,
                    CollectorType = target.CollectorType,
                    Label = target.Label,
                    Credentials = ToTargetCredentials(target.Credentials),
                }
            );
        }

        return merged;
    }

    /// <summary>
    /// Maps a server-delivered credential to the agent's target credential model.
    /// Returns null when the target has no credential.
    /// NOTE: ssh-password credentials carry only the password — the username is not
    /// stored server-side in this phase, so it defaults to "root". This is an
    /// accepted gap until the credential schema gains a username field.
    /// </summary>
    private static TargetCredentials? ToTargetCredentials(TargetCredential? credential)
    {
        if (credential is null)
        {
            return null;
        }

        return credential.Type switch
        {
            "ssh-key" => new SshCredentials
            {
                Username = "root",
                KeyFile = credential.Secret,
            },
            "ssh-password" => new SshCredentials
            {
                Username = "root",
                Password = credential.Secret,
            },
            "snmp" => new SnmpCredentials
            {
                Community = credential.Secret,
            },
            "api-token" => new ApiTokenCredentials
            {
                Token = credential.Secret,
            },
            _ => null,
        };
    }

    private sealed record LocalCollectResult(
        FactBatchElement? Element,
        IReadOnlyList<CollectorStat> CollectorStats,
        IReadOnlyList<ScannerStat> ScannerStats
    );

    private sealed record CollectorRunResult(string Name, IReadOnlyList<Fact> Facts, int DurationMs, string? Error);

    private async Task<IReadOnlyList<Fingerprint>> GetLocalFingerprintsAsync(CancellationToken ct)
    {
        if (_cachedFingerprints is not null
         && DateTimeOffset.UtcNow - _fingerprintsCachedAt < FingerprintCacheLifetime)
        {
            return _cachedFingerprints;
        }

        _cachedFingerprints = await LocalMachineFingerprints.CollectAsync(ct);
        _fingerprintsCachedAt = DateTimeOffset.UtcNow;
        return _cachedFingerprints;
    }

    // runDiscovery gates NetworkDiscoveryCollector (subnet discovery); runInventory gates the
    // host-inventory collectors. Split so the two phases can run on their own cadences.
    private async Task<LocalCollectResult> CollectLocalAsync(bool runDiscovery, bool runInventory, CancellationToken ct)
    {
        IReadOnlyList<Fingerprint> fingerprints = await GetLocalFingerprintsAsync(ct);
        if (fingerprints.Count == 0)
        {
            AgentMessages.LocalNoFingerprints(_logger);
            return new LocalCollectResult(null, [], []);
        }

        string trackerKey = FingerprintsHash(fingerprints);
        CollectorDeltaTracker tracker = GetOrCreateTracker(trackerKey);

        // Local collectors take a placeholder device key — the server rewrites it.
        const string placeholder = "_local_";

        // Run enabled local collectors in parallel — each produces its own facts with no shared state.
        List<Task<CollectorRunResult>> collectTasks = _localCollectors
            .Where(c => IsCollectorEnabled(c.GetType().Name))
            .Where(c => c is NetworkDiscoveryCollector ? runDiscovery : runInventory)
            .Select(c => CollectOneLocalWithStatsAsync(c, placeholder, ct))
            .ToList();

        CollectorRunResult[] runResults = await Task.WhenAll(collectTasks);

        // Build per-collector stats.
        List<CollectorStat> collectorStats = runResults
            .Select(r => new CollectorStat(r.Name, r.Facts.Count, r.DurationMs, r.Error))
            .ToList();

        // Get scanner stats from NetworkDiscoveryCollector (populated after its run).
        List<ScannerStat> scannerStats = [];
        NetworkDiscoveryCollector? ndc = _localCollectors.OfType<NetworkDiscoveryCollector>().FirstOrDefault();
        if (ndc is not null)
        {
            scannerStats = [.. ndc.LastScannerStats];
        }

        List<Fact> allFacts = new();
        foreach (CollectorRunResult r in runResults)
        {
            allFacts.AddRange(r.Facts);
        }

        if (allFacts.Count == 0)
        {
            return new LocalCollectResult(null, collectorStats, scannerStats);
        }

        // Agents emit RAW facts; the server normalizes + derives at ingest. The delta tracker still
        // filters here purely to avoid re-sending unchanged data over the wire.
        IReadOnlyList<Fact> changed = tracker.FilterChanged(allFacts);

        // Always emit an element when we have fingerprints, even with zero changed facts. A
        // fingerprints-only element is a LIVENESS TOUCH: the server re-resolves the device and
        // stamps device_fingerprints.last_seen (the signal the liveness window keys on) so a static
        // host is never wrongly aged out of the live view just because its facts stopped changing.
        // The server ingests facts only when Facts is non-empty, so an empty-facts element adds no
        // projection churn — it is pure "still here".
        FactBatchElement element = new(fingerprints, changed);
        return new LocalCollectResult(element, collectorStats, scannerStats);
    }

    // Backfills FactSource on every fact still Unknown, keyed by the collector-kind
    // identifier (ILocalCollector.Name / Target.CollectorType / IServiceCollector.ServiceType).
    // NetworkDiscoveryCollector stamps its own facts per-scanner BEFORE returning them (see
    // NetworkDiscoveryCollector.KeyToSource), so this backfill only fills in facts no more
    // specific stamp already touched -- it never overwrites a real source with a generic one.
    private static readonly Dictionary<string, FactSource> LocalCollectorSources = new(StringComparer.Ordinal)
    {
        ["network-discovery"] = FactSource.NetworkDiscovery,
        ["dhcp-leases"] = FactSource.DhcpLeasesLocal,
        ["arp"] = FactSource.ArpLocal,
        ["cert-scan"] = FactSource.CertScan,
        ["battery"] = FactSource.Battery,
        ["filesystem"] = FactSource.Filesystem,
        ["disk"] = FactSource.Disk,
        ["docker"] = FactSource.Docker,
        ["network"] = FactSource.NetworkLocal,
        ["gpu"] = FactSource.Gpu,
        ["hw-inventory"] = FactSource.HwInventory,
        ["port"] = FactSource.Port,
        ["packages"] = FactSource.Packages,
        ["hardware"] = FactSource.Hardware,
        ["os"] = FactSource.Os,
        ["security"] = FactSource.Security,
        ["routes"] = FactSource.Routes,
        ["process"] = FactSource.Process,
        ["step-client"] = FactSource.StepClient,
        ["updates"] = FactSource.Updates,
        ["service"] = FactSource.ServiceLocal,
        ["reboot-history"] = FactSource.RebootHistory,
        ["user"] = FactSource.User,
        ["step-ca"] = FactSource.StepCa,
    };

    private static readonly Dictionary<string, FactSource> DeviceCollectorSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh"] = FactSource.Ssh,
        ["snmp"] = FactSource.SnmpDevice,
        ["bacnet"] = FactSource.BacnetDevice,
        ["modbus"] = FactSource.ModbusDevice,
        ["google-wifi"] = FactSource.GoogleWifi,
    };

    private static readonly Dictionary<string, FactSource> ServiceCollectorSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["technitium-dns"] = FactSource.TechnitiumDns,
        ["home-assistant"] = FactSource.HomeAssistant,
    };

    private static IReadOnlyList<Fact> BackfillSource(IReadOnlyList<Fact> facts, FactSource fallback)
    {
        if (fallback == FactSource.Unknown)
        {
            return facts;
        }

        bool anyUnknown = false;
        foreach (Fact f in facts)
        {
            if (f.Source == FactSource.Unknown)
            {
                anyUnknown = true;
                break;
            }
        }

        if (!anyUnknown)
        {
            return facts;
        }

        Fact[] result = new Fact[facts.Count];
        for (int i = 0; i < facts.Count; i++)
        {
            result[i] = facts[i].Source == FactSource.Unknown ? facts[i] with { Source = fallback } : facts[i];
        }

        return result;
    }

    // A collector's DeviceIdentity carries Kind/Vendor/OsFamily/OsVersion for device
    // resolution, but nothing downstream ever reads those fields past RegisterProbeAsync —
    // only ResolvedFingerprints feeds the FactBatchElement. A collector that sets one of
    // these without ALSO emitting an explicit fact for it (e.g. SnmpCollector's sysObjectID-
    // resolved Vendor, or Bacnet/Modbus/Ssh/Snmp's Kind) had that value silently discarded.
    // This fills the gap by emitting the missing fact from the identity — but only when the
    // collector didn't already emit one explicitly, so e.g. GoogleWifiCollector's redundant
    // Device[].Vendor fact isn't double-written.
    private static IReadOnlyList<Fact> AppendIdentityFacts(
        IReadOnlyList<Fact> facts,
        DeviceIdentity? identity,
        string? devicePlaceholder
    )
    {
        if (identity is null || devicePlaceholder is null)
        {
            return facts;
        }

        List<Fact>? additions = null;
        AddIfMissing(FactPaths.DeviceKind, identity.Kind);
        AddIfMissing(FactPaths.DeviceVendor, identity.Vendor);
        AddIfMissing(FactPaths.SystemOsFamily, identity.OsFamily);
        AddIfMissing(FactPaths.SystemOsVersion, identity.OsVersion);

        if (additions is null)
        {
            return facts;
        }

        List<Fact> combined = new(facts.Count + additions.Count);
        combined.AddRange(facts);
        combined.AddRange(additions);
        return combined;

        void AddIfMissing(string path, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (Fact f in facts)
            {
                if (f.AttributePath == path)
                {
                    return; // collector already emitted this explicitly — don't double-write
                }
            }

            (additions ??= []).Add(Fact.Create(path, [devicePlaceholder], value));
        }
    }

    private async Task<CollectorRunResult> CollectOneLocalWithStatsAsync(
        ILocalCollector collector,
        string placeholder,
        CancellationToken ct
    )
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<Fact> facts = await collector.CollectAsync(placeholder, ct);
            facts = BackfillSource(facts, LocalCollectorSources.GetValueOrDefault(collector.Name, FactSource.Unknown));
            return new CollectorRunResult(collector.Name, facts, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AgentMessages.LocalCollectorFailed(_logger, ex, collector.Name);
            return new CollectorRunResult(collector.Name, [], (int)sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Collects one remote target and records a per-target activity stat (facts
    /// collected, duration, error) for the cycle summary — the "device scanner"
    /// counterpart to CollectorStat/ScannerStat.
    /// </summary>
    private async Task<(FactBatchElement? Element, DeviceScannerStat Stat)> CollectDeviceWithStatsAsync(
        Target target,
        CancellationToken ct
    )
    {
        long start = Stopwatch.GetTimestamp();
        FactBatchElement? element = null;
        int rawFacts = 0;
        string? error = null;
        try
        {
            (element, rawFacts) = await CollectDeviceAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            AgentMessages.CollectionFailed(_logger, ex, target.Endpoint);
        }

        int durationMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        DeviceScannerStat stat = new(target.Endpoint, target.CollectorType ?? "any", rawFacts, durationMs, error);
        return (element, stat);
    }

    private async Task<(FactBatchElement? Element, int RawFacts)> CollectDeviceAsync(
        Target target,
        CancellationToken ct
    )
    {
        IDeviceCollector? collector = _collectors.FirstOrDefault(c => c.CanCollect(target));
        if (collector is null)
        {
            string collectorType = target.CollectorType ?? "any";
            AgentMessages.NoCollectorForTarget(_logger, target.Endpoint, collectorType);
            return (null, 0);
        }

        CollectionContext context = new(AgentId);
        IReadOnlyList<Fact> rawFacts = await collector.CollectAsync(target, context, ct);
        rawFacts = AppendIdentityFacts(rawFacts, context.ResolvedIdentity, context.DevicePlaceholder);
        rawFacts = BackfillSource(
            rawFacts,
            DeviceCollectorSources.GetValueOrDefault(target.CollectorType ?? "", FactSource.Unknown)
        );

        if (rawFacts.Count == 0)
        {
            return (null, 0);
        }

        IReadOnlyList<Fingerprint>? fingerprints = context.ResolvedFingerprints;
        if (fingerprints is null || fingerprints.Count == 0)
        {
            AgentMessages.CollectorNoFingerprints(_logger, target.Endpoint);
            return (null, rawFacts.Count);
        }

        string trackerKey = FingerprintsHash(fingerprints);
        CollectorDeltaTracker tracker = GetOrCreateTracker(trackerKey);
        // Raw facts over the wire; the server normalizes. Tracker only suppresses unchanged data.
        IReadOnlyList<Fact> changed = tracker.FilterChanged(rawFacts);

        // Even when nothing changed, emit a fingerprints-only element as a LIVENESS TOUCH: it
        // re-resolves the device server-side and stamps device_fingerprints.last_seen so a static
        // polled device isn't wrongly hidden by the liveness window. The server ingests facts only
        // when Facts is non-empty, so an empty-facts element is pure "still here" with no churn.
        return (new FactBatchElement(fingerprints, changed), rawFacts.Count);
    }

    private async Task<List<ServiceStat>> CollectServicesAsync(
        List<Target> services,
        CancellationToken ct
    )
    {
        List<ServiceStat> stats = new(services.Count);
        foreach (Target target in services)
        {
            string label = target.Label ?? target.Endpoint;
            string collectorType = target.CollectorType ?? "any";
            Stopwatch sw = Stopwatch.StartNew();

            IServiceCollector? collector = _serviceCollectors.FirstOrDefault(c => c.CanCollect(target));
            if (collector is null)
            {
                AgentMessages.NoServiceCollector(_logger, collectorType, target.Endpoint);
                stats.Add(new ServiceStat(label, collectorType, 0, (int)sw.ElapsedMilliseconds, "no collector"));
                continue;
            }

            try
            {
                // Link the service to this host only when the target is local —
                // for remote URLs the hosting device is unknown.
                string? hostDeviceId = IsLoopbackUrl(target.Endpoint) ? _localDeviceId : null;
                ServiceCollectionContext context = new(AgentId, hostDeviceId);
                IReadOnlyList<Fact> rawFacts = await collector.CollectAsync(target, context, ct);
                rawFacts = BackfillSource(
                    rawFacts,
                    ServiceCollectorSources.GetValueOrDefault(collector.ServiceType, FactSource.Unknown)
                );

                int sent = 0;
                if (rawFacts.Count > 0 && context.Probe is not null)
                {
                    string cacheKey = $"svc:{target.Endpoint}";
                    CollectorDeltaTracker tracker = GetOrCreateTracker(cacheKey);
                    // Raw facts over the wire; the server normalizes. Tracker only suppresses unchanged data.
                    IReadOnlyList<Fact> changed = tracker.FilterChanged(rawFacts);

                    if (changed.Count > 0)
                    {
                        // Service batches carry the identity probe instead of device
                        // fingerprints — the server resolves a stable ServiceId from the
                        // probe's logical fingerprints and rewrites fact roots to
                        // Service[{serviceId}].
                        List<Fact> outgoing = new(changed.Count + 1);
                        outgoing.AddRange(changed);

                        // Loopback services already link to this host (hostDeviceId →
                        // Service[].DeviceId). For a remotely-polled service the agent can't know
                        // the server's DeviceId, so emit the endpoint IP it connects to — resolved
                        // the same way the connection resolves it — and let the server map that IP
                        // to the host device. Placeholder root key is rewritten to the ServiceId.
                        if (hostDeviceId is null)
                        {
                            string? endpointIp = await ResolveEndpointAddressAsync(target.Endpoint, ct);
                            if (endpointIp is not null)
                            {
                                outgoing.Add(Fact.Create(ServicePaths.Address, ["service"], endpointIp));
                            }
                        }

                        FactBatchElement batchElement = new([], outgoing, context.Probe);
                        AgentFactsRequest request = new(
                            AgentId: Guid.Parse(AgentId),
                            CollectedAt: DateTimeOffset.UtcNow,
                            FactBatches: [batchElement]
                        );

                        try
                        {
                            await _server.PostFactsAsync(ApiKey, request, ct);
                            sent = changed.Count;
                            tracker.Save(TrackerFilePath(cacheKey));
                        }
                        catch (Exception ex)
                        {
                            AgentMessages.ServiceFactsPostFailed(_logger, ex, label);
                            tracker.Rollback();
                            throw;
                        }
                    }
                }

                stats.Add(new ServiceStat(label, collectorType, sent, (int)sw.ElapsedMilliseconds, null));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AgentMessages.ServiceCollectionFailed(_logger, ex, label);
                stats.Add(new ServiceStat(label, collectorType, 0, (int)sw.ElapsedMilliseconds, ex.GetType().Name));
            }
        }

        return stats;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsLoopbackUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;

    /// <summary>
    /// Resolves a service endpoint URL's host to an IP the same way the connection would: an IP
    /// literal is returned as-is; a hostname is resolved via DNS (preferring IPv4). Returns null
    /// when the endpoint can't be parsed or resolved, so the server leaves the service unlinked
    /// rather than guess. Cancellation propagates.
    /// </summary>
    private static async Task<string?> ResolveEndpointAddressAsync(string endpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            return literal.ToString();
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            IPAddress? preferred = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? (addresses.Length > 0 ? addresses[0] : null);
            return preferred?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private CollectorDeltaTracker GetOrCreateTracker(string key)
    {
        if (!_trackers.TryGetValue(key, out CollectorDeltaTracker? tracker))
        {
            tracker = CollectorDeltaTracker.LoadOrCreate(TrackerFilePath(key));
            _trackers[key] = tracker;
        }

        return tracker;
    }

    // Tracker keys aren't filename-safe as-is (e.g. "svc:https://host/path") — hash to a
    // stable, safe filename instead of trying to sanitize the key.
    private string TrackerFilePath(string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return Path.Combine(_stateDir, $"tracker-{Convert.ToHexStringLower(hash[..8])}.json");
    }

    private void PersistAllTrackers()
    {
        foreach ((string key, CollectorDeltaTracker tracker) in _trackers)
        {
            tracker.Save(TrackerFilePath(key));
        }
    }

    internal static string FingerprintsHash(IReadOnlyList<Fingerprint> fps)
    {
        int count = fps.Count;

        // Sort by (Type, Value) without LINQ allocations.
        Fingerprint[] arr = new Fingerprint[count];
        for (int i = 0; i < count; i++) { arr[i] = fps[i]; }

        Array.Sort(
            arr,
            static (a, b) =>
            {
                int t = string.CompareOrdinal(a.Type, b.Type);
                return t != 0 ? t : string.CompareOrdinal(a.Value, b.Value);
            }
        );

        // Hash incrementally — avoids materializing the canonical string and intermediate byte array.
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buf = stackalloc byte[256];
        for (int i = 0; i < count; i++)
        {
            if (i > 0) { hasher.AppendData("|"u8); }

            hasher.AppendData(buf[..Encoding.UTF8.GetBytes(arr[i].Type, buf)]);
            hasher.AppendData(":"u8);
            hasher.AppendData(buf[..Encoding.UTF8.GetBytes(arr[i].Value, buf)]);
        }

        Span<byte> hashBytes = stackalloc byte[32];
        hasher.GetHashAndReset(hashBytes);
        return Convert.ToHexStringLower(hashBytes[..8]); // first 8 bytes = 16 hex chars
    }

    // ── State persistence ─────────────────────────────────────────────────────

    private string AgentIdPath => Path.Combine(_stateDir, "agent-id");
    private string ApiKeyPath => Path.Combine(_stateDir, "api-key");
    private string ClearTrackersMarkerPath => Path.Combine(_stateDir, "trackers-cleared-at");
    private string LogsUploadedMarkerPath => Path.Combine(_stateDir, "logs-uploaded-at");

    private void LoadState()
    {
        if (File.Exists(AgentIdPath))
        {
            _agentId = File.ReadAllText(AgentIdPath).Trim();
        }

        if (File.Exists(ApiKeyPath))
        {
            _apiKey = File.ReadAllText(ApiKeyPath).Trim();
        }

        if (File.Exists(ClearTrackersMarkerPath)
         && DateTimeOffset.TryParse(File.ReadAllText(ClearTrackersMarkerPath).Trim(), out DateTimeOffset marker))
        {
            _lastTrackersClearedAt = marker;
        }

        if (File.Exists(LogsUploadedMarkerPath)
         && DateTimeOffset.TryParse(File.ReadAllText(LogsUploadedMarkerPath).Trim(), out DateTimeOffset logsMarker))
        {
            _lastLogsUploadedAt = logsMarker;
        }
    }

    /// <summary>
    /// Wipes all local delta-tracker cache files when the server requests a clear that's
    /// newer than the last one this agent already acted on. Needed when server-side data
    /// (e.g. a projection table) is reset independently of the agent — the cache would
    /// otherwise keep thinking it already reported facts the server has no record of, and
    /// silently withhold them until something about them happens to change.
    /// </summary>
    private void ApplyClearTrackersRequestIfNeeded(DateTimeOffset? requestedAt)
    {
        if (requestedAt is not { } requested
         || (_lastTrackersClearedAt is { } last && requested <= last))
        {
            return;
        }

        _trackers.Clear();
        foreach (string file in Directory.EnumerateFiles(_stateDir, "tracker-*.json"))
        {
            File.Delete(file);
        }

        _lastTrackersClearedAt = requested;
        File.WriteAllText(ClearTrackersMarkerPath, requested.ToString("O"));
        AgentMessages.TrackersCleared(_logger, requested);
    }

    /// <summary>
    /// When the server requests a log pull newer than the last one this agent acted on, captures a
    /// page of recent console/journald output (§4.1) and uploads it to the server's in-memory cache.
    /// The local marker is advanced regardless of upload success — a server hiccup should not make
    /// the agent re-capture and retry-storm on every subsequent heartbeat; the admin can just click
    /// again. Honors the requested page size and paging token (docs/plans/agent-log-viewer.md §4.2).
    /// </summary>
    private async Task ApplyLogsRequestIfNeeded(
        DateTimeOffset? requestedAt,
        int? requestedLines,
        string? before,
        CancellationToken ct
    )
    {
        if (requestedAt is not { } requested
         || (_lastLogsUploadedAt is { } last && requested <= last))
        {
            return;
        }

        _lastLogsUploadedAt = requested;
        File.WriteAllText(LogsUploadedMarkerPath, requested.ToString("O"));

        try
        {
            int lines = requestedLines is { } n && n > 0 ? n : 500;
            AgentLogCollector.LogPage page = await _logCollector.CaptureAsync(lines, before, ct);

            await _server.PostLogsAsync(
                ApiKey,
                new AgentLogUploadRequest(
                    AgentId: Guid.Parse(AgentId),
                    RequestedAt: requested,
                    Source: page.Source,
                    Truncated: page.Truncated,
                    Text: page.Text,
                    NextBeforeToken: page.NextBeforeToken
                ),
                ct
            );

            AgentMessages.LogsUploaded(_logger, page.Source, requested);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-fatal — the marker is already advanced, so we won't retry-storm.
            AgentMessages.LogsUploadFailed(_logger, ex);
        }
    }

    private void SaveAgentId() =>
        File.WriteAllText(AgentIdPath, _agentId);

    private void SaveApiKey()
    {
        File.WriteAllText(ApiKeyPath, _apiKey);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ApiKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string DetectOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macos";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        return "unknown";
    }

    private static string? DetectPrimaryIp()
    {
        try
        {
            using Socket socket = new(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp
            );
            socket.Connect("8.8.8.8", 53);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception ex)
        {
            ILogger logger = AgentLog.CreateLogger<Agent>();
            AgentMessages.PrimaryIpDetectFailed(logger, ex);
            return null;
        }
    }
}

// ── ICollectionContext implementation ─────────────────────────────────────────

/// <summary>
/// Container-owned implementation of ICollectionContext.
/// Stores the fingerprints the collector registers so the agent can
/// assemble the FactBatchElement after collection completes.
/// </summary>
internal sealed class CollectionContext : ICollectionContext
{
    private readonly string _agentId;

    public CollectionContext(string agentId)
    {
        _agentId = agentId;
    }

    public string AgentId => _agentId;
    public IReadOnlyList<Fingerprint>? ResolvedFingerprints { get; private set; }
    public DeviceIdentity? ResolvedIdentity { get; private set; }

    // Placeholder ID to use in fact ID segments — server rewrites to Device[{uuid}].
    // Collector must use this as the Device key when building fact IDs. Also read by
    // CollectDeviceAsync to key any auto-emitted identity facts (see AppendIdentityFacts)
    // with the same placeholder the collector used for its own facts.
    public string? DevicePlaceholder { get; private set; }

    public Task<string> RegisterProbeAsync(DeviceIdentity identity, CancellationToken ct)
    {
        ResolvedFingerprints = identity.Fingerprints;
        ResolvedIdentity = identity;

        // Build a stable placeholder from the fingerprints hash so fact IDs
        // are consistent within the collection session.
        DevicePlaceholder ??= "_probe_";
        return Task.FromResult(DevicePlaceholder);
    }
}

// ── IServiceCollectionContext implementation ──────────────────────────────────

internal sealed class ServiceCollectionContext : IServiceCollectionContext
{
    private readonly string? _hostDeviceId;
    private readonly string _agentId;

    public ServiceCollectionContext(
        string agentId,
        string? hostDeviceId
    )
    {
        _agentId = agentId;
        _hostDeviceId = hostDeviceId;
    }

    public string AgentId => _agentId;
    public string? HostDeviceId => _hostDeviceId;

    /// <summary>
    /// The probe the collector registered this session. The agent attaches it to
    /// the service fact batch so the server can resolve a stable ServiceId.
    /// </summary>
    public ServiceProbe? Probe { get; private set; }

    public Task<string> IdentifyServiceAsync(ServiceProbe probe, CancellationToken ct)
    {
        // Identity is resolved server-side from the probe when the batch is posted.
        // Locally, return a placeholder root that the server rewrites to
        // Service[{resolved_service_id}].
        Probe = probe;
        return Task.FromResult("_service_");
    }
}

// ── Version ───────────────────────────────────────────────────────────────────

internal static class AgentVersion
{
    /// <summary>
    /// The agent's version string, read from the assembly's InformationalVersion attribute
    /// which is set by <c>-p:Version=X.Y.Z</c> during <c>dotnet publish</c>.
    /// The CI/CD pipeline derives this from the git tag (e.g. v3.0.1 -> 3.0.1).
    /// Strips any build-metadata suffix (e.g. "3.0.1+abc123" -> "3.0.1").
    /// Falls back to "0.0.0-dev" for local development builds.
    /// </summary>
    public static readonly string Current =
        typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0-dev";
}

// ── Local machine fingerprinting ──────────────────────────────────────────────

/// <summary>
/// Collects stable fingerprints for the local machine so the server can
/// assign a persistent DeviceId across agent reinstalls and reboots.
/// </summary>
internal static class LocalMachineFingerprints
{
    public static async Task<IReadOnlyList<Fingerprint>> CollectAsync(CancellationToken ct = default)
    {
        List<Fingerprint> fps = new();

        if (OperatingSystem.IsMacOS())
        {
            await CollectMacOsFingerprintsAsync(fps, ct);
        }
        else if (OperatingSystem.IsLinux())
        {
            string? machineId = ReadMachineId();
            if (machineId is not null)
            {
                fps.Add(new Fingerprint(FingerprintType.MachineId, machineId));
            }

            CollectLinuxSerialFingerprints(fps);
        }
        else if (OperatingSystem.IsWindows())
        {
            string? machineId = ReadMachineId();
            if (machineId is not null)
            {
                fps.Add(new Fingerprint(FingerprintType.MachineId, machineId));
            }

            await CollectWindowsSerialFingerprintsAsync(fps, ct);
        }

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6)
            {
                continue;
            }

            if ((mac[0] & 0x02) != 0)
            {
                continue; // locally administered
            }

            if (mac.All(b => b == 0))
            {
                continue; // all-zeros
            }

            string normalized = Convert.ToHexString(mac).ToLowerInvariant();
            fps.Add(new Fingerprint(FingerprintType.Mac, normalized));
        }

        // Deduplicate by (Type, Value): a host may expose the same MAC on multiple
        // interfaces (a bond/bridge sharing the enslaved NIC's hardware address), which
        // would otherwise send duplicate fingerprints the server must reject.
        Dictionary<(string, string), Fingerprint> unique = new();
        foreach (Fingerprint fp in fps)
        {
            unique[(fp.Type, fp.Value)] = fp;
        }

        return [.. unique.Values];
    }

    // Collects the IOPlatformUUID (machine-id) from ioreg and the chassis serial
    // from system_profiler so both can be used for device identity matching on macOS.
    private static async Task CollectMacOsFingerprintsAsync(List<Fingerprint> fps, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi = new("ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return;
            }

            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            foreach (string line in output.Split('\n'))
            {
                if (!line.Contains("IOPlatformUUID"))
                {
                    continue;
                }

                string? uuid = ExtractIoregStringValue(line);
                if (uuid is not null)
                {
                    fps.Add(new Fingerprint(FingerprintType.MachineId, uuid.Replace("-", "").ToLowerInvariant()));
                }

                break;
            }
        }
        catch (Exception ex)
        {
            ILogger logger = AgentLog.CreateLogger<Agent>();
            AgentMessages.MacOsUuidReadFailed(logger, ex);
        }

        try
        {
            string profiler = await CollectorHelper.RunAsync("system_profiler", "SPHardwareDataType", ct);
            foreach (string line in profiler.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("Serial Number (system):", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string serial = trimmed["Serial Number (system):".Length..].Trim();
                if (serial.Length >= 4)
                {
                    fps.Add(new Fingerprint(FingerprintType.ChassisSerial, "apple:" + serial));
                }

                break;
            }
        }
        catch { }

        await CollectMacOsBluetoothMacAsync(fps, ct);
        await CollectMacOsDiskSerialsAsync(fps, ct);
    }

    // Reads the built-in Bluetooth controller's MAC from system_profiler. Only the
    // controller address is captured (not paired peripherals); it is a globally-
    // administered Apple MAC and a viable match point for Bluetooth-side discovery.
    private static async Task CollectMacOsBluetoothMacAsync(List<Fingerprint> fps, CancellationToken ct)
    {
        try
        {
            string profiler = await CollectorHelper.RunAsync("system_profiler", "SPBluetoothDataType", ct);
            bool inController = false;
            foreach (string line in profiler.Split('\n'))
            {
                string trimmed = line.Trim();

                // The controller section is the first block; peripheral addresses
                // live under "Connected:" / "Not Connected:" — stop before those.
                if (trimmed.StartsWith("Bluetooth Controller:", StringComparison.OrdinalIgnoreCase))
                {
                    inController = true;
                    continue;
                }

                if (trimmed is "Connected:" or "Not Connected:")
                {
                    break;
                }

                if (inController && trimmed.StartsWith("Address:", StringComparison.OrdinalIgnoreCase))
                {
                    string mac = trimmed["Address:".Length..].Trim();
                    fps.Add(new Fingerprint(FingerprintType.Mac, mac));
                    break;
                }
            }
        }
        catch { }
    }

    // Reads serials of internal, non-removable storage from system_profiler and
    // emits a DiskSerial fingerprint for each. Removable/external drives are
    // skipped because their serial identifies the disk, not the host device.
    private static async Task CollectMacOsDiskSerialsAsync(List<Fingerprint> fps, CancellationToken ct)
    {
        foreach (string dataType in new[]
        {
            "SPNVMeDataType",
            "SPSerialATADataType",
        })
        {
            try
            {
                string profiler = await CollectorHelper.RunAsync("system_profiler", dataType, ct);
                string? pendingVendor = null;
                string? pendingSerial = null;

                foreach (string line in profiler.Split('\n'))
                {
                    string trimmed = line.Trim();

                    if (trimmed.StartsWith("Model:", StringComparison.OrdinalIgnoreCase))
                    {
                        string model = trimmed["Model:".Length..].Trim();
                        string[] parts = model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        pendingVendor = parts.Length > 0 ? parts[0] : "disk";
                    }
                    else if (trimmed.StartsWith("Serial Number:", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingSerial = trimmed["Serial Number:".Length..].Trim();
                    }
                    else if (trimmed.StartsWith("Removable Media:", StringComparison.OrdinalIgnoreCase))
                    {
                        string removable = trimmed["Removable Media:".Length..].Trim();
                        if (removable.Equals("No", StringComparison.OrdinalIgnoreCase)
                         && pendingSerial is not null
                         && pendingSerial.Length >= 4)
                        {
                            string vendor = pendingVendor ?? "disk";
                            fps.Add(new Fingerprint(FingerprintType.DiskSerial, $"{vendor}:{pendingSerial}"));
                        }

                        pendingVendor = null;
                        pendingSerial = null;
                    }
                }
            }
            catch { }
        }
    }

    // Extracts the quoted value after '=' in an ioreg line like:
    //   "IOPlatformUUID" = "E54423DF-C72F-55D2-8A83-CD98F3EC4953"
    private static string? ExtractIoregStringValue(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            return null;
        }

        int open = line.IndexOf('"', eq);
        if (open < 0)
        {
            return null;
        }

        int close = line.LastIndexOf('"');
        if (close <= open)
        {
            return null;
        }

        return line[(open + 1)..close];
    }

    private static string? ReadMachineId()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (string path in new[]
            {
                "/etc/machine-id",
                "/var/lib/dbus/machine-id",
            })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string id = File.ReadAllText(path).Trim().Replace("-", "").ToLowerInvariant();
                if (id.Length >= 16)
                {
                    return id;
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            if (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null
            ) is string guid)
            {
                return guid.Replace("-", "").ToLowerInvariant();
            }
        }

        return null;
    }

    // Reads the system/chassis serial from DMI sysfs on Linux and emits a
    // ChassisSerial fingerprint for each non-trivial value found.
    private static void CollectLinuxSerialFingerprints(List<Fingerprint> fps)
    {
        static string? ReadDmi(string file)
        {
            try
            {
                string v = File.ReadAllText($"/sys/class/dmi/id/{file}").Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch { return null; }
        }

        string vendor = ReadDmi("sys_vendor") ?? "bare";
        string? productSerial = ReadDmi("product_serial");
        string? chassisSerial = ReadDmi("chassis_serial");

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? serial in new[]
        {
            productSerial,
            chassisSerial,
        })
        {
            if (serial is null || !seen.Add(serial))
            {
                continue;
            }

            fps.Add(new Fingerprint(FingerprintType.ChassisSerial, $"{vendor}:{serial}"));
        }
    }

    // Reads the system serial from WMI via PowerShell on Windows.
    [SupportedOSPlatform("windows")]
    private static async Task CollectWindowsSerialFingerprintsAsync(List<Fingerprint> fps, CancellationToken ct)
    {
        try
        {
            const string script = """
                $bios = Get-CimInstance Win32_BIOS | Select-Object -ExpandProperty SerialNumber
                $bios
                """;
            string serial = (await CollectorHelper.RunPsAsync(script, ct)).Trim();
            if (serial.Length >= 4)
            {
                fps.Add(new Fingerprint(FingerprintType.ChassisSerial, $"bare:{serial}"));
            }
        }
        catch { }
    }
}

// ── Source-generated logger messages ──────────────────────────────────────────

internal static partial class AgentMessages
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Registered as '{Name}' ({AgentId}). Waiting for approval in the server UI."
    )]
    public static partial void Registered(ILogger logger, string name, string? agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Approved. Starting collection.")]
    public static partial void Approved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat failed.")]
    public static partial void HeartbeatFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Server requested a cache clear ({RequestedAt}) — wiped local delta-tracker files."
    )]
    public static partial void TrackersCleared(ILogger logger, DateTimeOffset requestedAt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Server requested logs ({RequestedAt}) — uploaded a page from {Source}."
    )]
    public static partial void LogsUploaded(ILogger logger, string source, DateTimeOffset requestedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log upload failed.")]
    public static partial void LogsUploadFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UpdatePublicKey.Value is empty. This binary cannot receive self-updates. "
                + "Rebuild with a real signing key before deploying to production."
    )]
    public static partial void UpdatePublicKeyMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update offered: {Version}.")]
    public static partial void UpdateOffered(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot apply update: server client is not HTTP-based.")]
    public static partial void UpdateNotHttp(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Update apply failed.")]
    public static partial void UpdateFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Posted {BatchCount} batch(es), {FactCount} facts. Server accepted {AcceptedBatches} batch(es)."
    )]
    public static partial void FactsPosted(ILogger logger, int batchCount, int factCount, int acceptedBatches);

    [LoggerMessage(Level = LogLevel.Error, Message = "Facts post failed.")]
    public static partial void FactsPostFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local device: no fingerprints collected, skipping.")]
    public static partial void LocalNoFingerprints(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Local collector '{CollectorName}' failed.")]
    public static partial void LocalCollectorFailed(ILogger logger, Exception ex, string collectorName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No collector can handle {Address} (protocol: {Protocol}).")]
    public static partial void NoCollectorForTarget(ILogger logger, string address, string protocol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collector for {Address} produced no fingerprints — skipping.")]
    public static partial void CollectorNoFingerprints(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collection failed for {Address}.")]
    public static partial void CollectionFailed(ILogger logger, Exception ex, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No service collector handles type '{ServiceType}' ({Url}).")]
    public static partial void NoServiceCollector(ILogger logger, string serviceType, string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service facts post failed for {Label}; rolling back tracker.")]
    public static partial void ServiceFactsPostFailed(ILogger logger, Exception ex, string label);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service collection failed for {Label}.")]
    public static partial void ServiceCollectionFailed(ILogger logger, Exception ex, string label);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not detect primary IP address.")]
    public static partial void PrimaryIpDetectFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read macOS IOPlatformUUID.")]
    public static partial void MacOsUuidReadFailed(ILogger logger, Exception ex);
}