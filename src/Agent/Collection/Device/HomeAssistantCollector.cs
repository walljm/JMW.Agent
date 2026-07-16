using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Agent.Collection.Device.HomeAssistant;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Service collector for Home Assistant. One target, one credential: HA Core's own
/// device/entity registry — real, individually addressable network hardware HA tracks —
/// fetched over HA Core's WebSocket API (registry data is not exposed over REST) using a
/// user Long-Lived Access Token. This is the mandatory primary fetch; without a usable
/// token no facts are produced at all.
/// Additionally, best-effort, this collector polls the Supervisor REST API
/// (/supervisor/info, /core/info, /os/info, /host/info, /addons) for facts about the HA
/// instance itself (core/OS/add-on versions). That API only resolves against the internal
/// address http://supervisor — the address Docker gives an HA add-on container — and is
/// authenticated by the SUPERVISOR_TOKEN environment variable HA injects automatically into
/// add-ons. It is not reachable from outside HA and the Supervisor token is not a
/// Long-Lived Access Token — they are different auth systems HA never lets you swap. So
/// this half of the collection is silently skipped unless this agent process happens to be
/// running as the HA add-on itself.
/// Only devices with a MAC, or a MAC-less identifier from an allow-listed domain (network
/// printers, Nabu Casa USB radios — see <see cref="HasAllowedIdentifierDomain" />), are
/// promoted. Everything else HA's registry reports — integration/add-on bookkeeping entries
/// (<c>entry_type == "service"</c>: Backup, HACS, Met.no, Supervisor/Core/OS, etc.) and
/// MAC-less hardware from integrations that duplicate a device some other collector already
/// reports with a real MAC (Google Home cloud speakers, mobile-app phones, bare Zigbee/Z-Wave
/// nodes) — is deliberately not minted as a device.
/// Emits Service[serviceId].HomeAssistant.HaDevice[haDeviceId].* facts, one per surviving HA
/// device-registry entry; HomeAssistantDevicePromotion resolves each into its own Device[] row
/// inline during ingest (see docs/plans/ha-inline-discovery.md).
/// Target: Target.Endpoint = HA's base URL (e.g. https://ha.home:8123); Credentials =
/// ApiTokenCredentials with a Core Long-Lived Access Token (Profile → Security in the HA UI).
/// </summary>
public sealed class HomeAssistantCollector : IServiceCollector
{
    public string ServiceType => "home-assistant";

    public bool CanCollect(Target target) =>
        target.CollectorType is { } t && t.Equals("home-assistant", StringComparison.OrdinalIgnoreCase);

    // Home Assistant is commonly served over HTTPS with a private-CA certificate (e.g.
    // https://ha.home:8123). CaTrust makes this handler trust the OS system store plus any
    // CAs the server delivers via the heartbeat config, so validation succeeds without
    // disabling certificate checks.
    private static readonly SocketsHttpHandler _handler = CaTrust.CreateHandler();

    private static readonly HttpClient _http = new(_handler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<IHomeAssistantSocket> _socketFactory;
    private readonly ILogger<HomeAssistantCollector> _logger = AgentLog.CreateLogger<HomeAssistantCollector>();

    public HomeAssistantCollector() : this(static () => new HomeAssistantClientWebSocket()) { }

    /// <summary>Test seam: inject a fake socket factory instead of a real ClientWebSocket.</summary>
    public HomeAssistantCollector(Func<IHomeAssistantSocket> socketFactory)
    {
        _socketFactory = socketFactory;
    }

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        IServiceCollectionContext context,
        CancellationToken ct
    )
    {
        List<Fact> facts = new();
        string baseUrl = target.Endpoint.TrimEnd('/');

        string? token = ResolveCoreToken(target.Credentials);
        if (token is null)
        {
            HomeAssistantCollectorLog.NoToken(_logger, baseUrl);
            return facts;
        }

        IHomeAssistantSocket socket = _socketFactory();
        HomeAssistantClient client = new(socket);

        IReadOnlyList<HaDevice>? devices;
        IReadOnlyList<HaEntity> entities;
        IReadOnlyList<HaArea> areas;
        IReadOnlyList<HaState> states;

        try
        {
            bool authenticated;
            try
            {
                authenticated = await client.ConnectAndAuthenticateAsync(BuildWebSocketUri(baseUrl), token, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HomeAssistantCollectorLog.ConnectFailed(_logger, ex, baseUrl);
                return facts;
            }

            if (!authenticated)
            {
                HomeAssistantCollectorLog.AuthInvalid(_logger, baseUrl);
                return facts;
            }

            // The device registry is essential — without it there is nothing to key facts on,
            // so a failure here aborts the poll. Entity registry / areas / states are
            // enrichment: each is independent, and losing one just narrows what a device
            // carries this cycle rather than losing the device entirely.
            devices = await TryRunAsync(() => client.GetDeviceRegistryAsync(ct), "config/device_registry/list", baseUrl);
            if (devices is null)
            {
                return facts;
            }

            entities = await TryRunAsync(() => client.GetEntityRegistryAsync(ct), "config/entity_registry/list", baseUrl)
                ?? [];
            areas = await TryRunAsync(() => client.GetAreaRegistryAsync(ct), "config/area_registry/list", baseUrl) ?? [];
            states = await TryRunAsync(() => client.GetStatesAsync(ct), "get_states", baseUrl) ?? [];
        }
        finally
        {
            await socket.DisposeAsync();
        }

        List<ServiceFingerprint> fingerprints = [new(ServiceFingerprintType.ServiceUrl, baseUrl)];
        string serviceId = await context.IdentifyServiceAsync(new ServiceProbe("home-assistant", fingerprints), ct);

        facts.Add(Fact.Create(ServicePaths.Type, [serviceId], "home-assistant"));
        facts.AddIfPresent(ServicePaths.HomeAssistantDeviceId, [serviceId], context.HostDeviceId);

        Dictionary<string, string> areaNames = areas.ToDictionary(a => a.AreaId, a => a.Name);
        Dictionary<string, HaState> statesByEntity = states.ToDictionary(s => s.EntityId, s => s);
        Dictionary<string, List<HaEntity>> entitiesByDevice = entities
            .Where(e => e.DeviceId is { Length: > 0 })
            .GroupBy(e => e.DeviceId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (HaDevice device in devices)
        {
            if (device.Disabled)
            {
                continue;
            }

            // entry_type "service" marks HA's own integration/add-on bookkeeping entries
            // (e.g. Backup, HACS, Met.no, Google Translate, Supervisor/Core/OS) rather than
            // physical hardware — these aren't devices on the network and shouldn't mint one.
            if (device.EntryType == "service")
            {
                HomeAssistantCollectorLog.ServiceEntry(_logger, device.Id, baseUrl);
                continue;
            }

            string? mac = device.Connections
                .FirstOrDefault(c => c.Type.Equals("mac", StringComparison.OrdinalIgnoreCase))
                .Value
                ?? MacFromIdentifiers(device);
            List<string> upnpUuids = ExtractUpnpUuids(device);

            // Without a MAC, only mint a device for identifier domains known to identify real,
            // individually-addressable hardware (network printers via IPP; Nabu Casa
            // USB radios via homeassistant_*; UPnP devices with a parseable device UUID).
            // Every other MAC-less domain observed in practice — google_home, mobile_app,
            // vesync, bare upnp_serial_number, zha, etc. — either duplicates a device some
            // other integration/collector already reports with a real MAC, or has no reliable
            // per-unit identity at all.
            if (string.IsNullOrEmpty(mac) && upnpUuids.Count == 0 && !HasAllowedIdentifierDomain(device))
            {
                HomeAssistantCollectorLog.NoIdentity(_logger, device.Id, baseUrl);
                continue;
            }

            string? identifiers = device.Identifiers.Count == 0
                ? null
                : string.Join('|', device.Identifiers.Select(id => $"{id.Domain}:{id.Value}"));

            AddDeviceFacts(facts, serviceId, device, mac, identifiers, upnpUuids, areaNames);
            AddHealthFacts(facts, serviceId, device.Id, entitiesByDevice.GetValueOrDefault(device.Id) ?? [], statesByEntity);
        }

        // Best-effort secondary fetch: only resolves when this agent process is running as
        // the HA add-on itself (see class remarks). Never blocks or fails the primary
        // device-registry facts above.
        string? supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (supervisorToken is { Length: > 0 })
        {
            await CollectSupervisorInfoAsync(facts, serviceId, supervisorToken, ct);
        }

        return facts;
    }

    private async Task CollectSupervisorInfoAsync(
        List<Fact> facts,
        string serviceId,
        string supervisorToken,
        CancellationToken ct
    )
    {
        const string supervisorBaseUrl = "http://supervisor";

        JsonElement? supervisorData = await GetDataAsync(supervisorBaseUrl, "/supervisor/info", supervisorToken, ct);
        if (supervisorData is { } sv)
        {
            facts.AddIfPresent(ServicePaths.HomeAssistantSupervisorVersion, [serviceId], sv.GetStr("version"));
            facts.AddIfPresent(ServicePaths.HomeAssistantChannel, [serviceId], sv.GetStr("channel"));
        }

        JsonElement? coreData = await GetDataAsync(supervisorBaseUrl, "/core/info", supervisorToken, ct);
        if (coreData is { } co)
        {
            facts.AddIfPresent(ServicePaths.HomeAssistantCoreVersion, [serviceId], co.GetStr("version"));
        }

        JsonElement? osData = await GetDataAsync(supervisorBaseUrl, "/os/info", supervisorToken, ct);
        if (osData is { } os)
        {
            facts.AddIfPresent(ServicePaths.HomeAssistantOsVersion, [serviceId], os.GetStr("version"));
            facts.AddIfPresent(ServicePaths.HomeAssistantOsBoard, [serviceId], os.GetStr("board"));
        }

        JsonElement? hostData = await GetDataAsync(supervisorBaseUrl, "/host/info", supervisorToken, ct);
        if (hostData is { } host)
        {
            facts.AddIfPresent(ServicePaths.HomeAssistantHostname, [serviceId], host.GetStr("hostname"));
        }

        JsonElement? addonsData = await GetDataAsync(supervisorBaseUrl, "/addons", supervisorToken, ct);
        if (addonsData is { } ad
         && ad.TryGetProperty("addons", out JsonElement addonsArray)
         && addonsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement addon in addonsArray.EnumerateArray())
            {
                string? slug = addon.GetStr("slug");
                if (slug is not { Length: > 0 })
                {
                    continue;
                }

                string[] addonKeys = [serviceId, slug];

                facts.AddIfPresent(ServicePaths.HomeAssistantAddOnName, addonKeys, addon.GetStr("name"));
                facts.AddIfPresent(ServicePaths.HomeAssistantAddOnVersion, addonKeys, addon.GetStr("version"));
                facts.AddIfPresent(ServicePaths.HomeAssistantAddOnState, addonKeys, addon.GetStr("state"));

                if (addon.TryGetProperty("update_available", out JsonElement ua)
                 && (ua.ValueKind == JsonValueKind.True || ua.ValueKind == JsonValueKind.False))
                {
                    facts.Add(Fact.Create(ServicePaths.HomeAssistantAddOnUpdateAvailable, addonKeys, ua.GetBoolean()));
                }
            }
        }
    }

    private static void AddDeviceFacts(
        List<Fact> facts,
        string serviceId,
        HaDevice device,
        string? mac,
        string? identifiers,
        List<string> upnpUuids,
        Dictionary<string, string> areaNames
    )
    {
        string[] keys = [serviceId, device.Id];

        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceMac, keys, mac);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceUpnpUuid, keys, upnpUuids.Count == 0 ? null : string.Join('|', upnpUuids));
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceIdentifiers, keys, identifiers);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceManufacturer, keys, device.Manufacturer);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceModel, keys, device.Model);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceModelId, keys, device.ModelId);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceHwVersion, keys, device.HwVersion);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceSwVersion, keys, device.SwVersion);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceSerialNumber, keys, device.SerialNumber);
        facts.AddIfPresent(
            ServicePaths.HomeAssistantHaDeviceLabels,
            keys,
            device.Labels.Count == 0 ? null : string.Join('|', device.Labels)
        );
        // Prefer the homeowner's own name over the integration-assigned default.
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceName, keys, device.NameByUser ?? device.Name);
        facts.AddIfPresent(ServicePaths.HomeAssistantHaDeviceViaDeviceKey, keys, device.ViaDeviceId);

        if (device.AreaId is { Length: > 0 } areaId && areaNames.TryGetValue(areaId, out string? areaName))
        {
            facts.Add(Fact.Create(ServicePaths.HomeAssistantHaDeviceAreaName, keys, areaName));
        }
    }

    /// <summary>
    /// Mines connectivity/battery/firmware-update signals from the device's own entities.
    /// Bulk sensor telemetry (temperature, humidity, power, etc.) is deliberately not
    /// collected — that's HA's job, not this agent's; pulling it in would turn every poll
    /// into a firehose disproportionate to the rest of the fact set.
    /// </summary>
    private static void AddHealthFacts(
        List<Fact> facts,
        string serviceId,
        string deviceId,
        List<HaEntity> deviceEntities,
        Dictionary<string, HaState> statesByEntity
    )
    {
        string[] keys = [serviceId, deviceId];

        foreach (HaEntity entity in deviceEntities)
        {
            if (!statesByEntity.TryGetValue(entity.EntityId, out HaState? state))
            {
                continue;
            }

            int dot = entity.EntityId.IndexOf('.');
            string domain = dot > 0 ? entity.EntityId[..dot] : "";
            string? deviceClass = state.Attributes.ValueKind == JsonValueKind.Object
                ? state.Attributes.GetStr("device_class")
                : null;

            switch (domain)
            {
                case "binary_sensor" when deviceClass == "connectivity":
                    if (state.State is "on" or "off")
                    {
                        facts.Add(Fact.Create(ServicePaths.HomeAssistantHaDeviceOnline, keys, state.State == "on"));
                    }

                    break;

                case "sensor" when deviceClass == "battery":
                    if (long.TryParse(state.State, out long batteryPercent))
                    {
                        facts.Add(Fact.Create(ServicePaths.HomeAssistantHaDeviceBatteryPercent, keys, batteryPercent));
                    }

                    break;

                case "update":
                    if (state.State is "on" or "off")
                    {
                        facts.Add(
                            Fact.Create(ServicePaths.HomeAssistantHaDeviceUpdateAvailable, keys, state.State == "on")
                        );
                    }

                    if (state.Attributes.ValueKind == JsonValueKind.Object
                     && state.Attributes.GetStr("latest_version") is { Length: > 0 } latestVersion)
                    {
                        facts.Add(Fact.Create(ServicePaths.HomeAssistantHaDeviceLatestVersion, keys, latestVersion));
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// MAC-less identifier domains allowed to mint a device on their own: "ipp" (network
    /// printers — HA's IPP integration is often the only source of a per-printer identity)
    /// and any "homeassistant_*" domain (Nabu Casa USB radios — SkyConnect, ZBT-1/2, Yellow —
    /// whose identifier value is the device's own USB serial/MAC-derived id).
    /// </summary>
    private static bool HasAllowedIdentifierDomain(HaDevice device) =>
        device.Identifiers.Any(
            id => id.Domain.Equals("ipp", StringComparison.OrdinalIgnoreCase)
             || id.Domain.StartsWith("homeassistant_", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Fallback MAC for registry entries with no "mac" connection pair: Nabu Casa USB radios
    /// (Connect ZBT-2 et al.) register identifiers-only, with the radio's EUI-48 as the
    /// identifier value (e.g. ["homeassistant_connect_zbt2", "1CDBD45E6F90"]). When that value
    /// is exactly 12 hex digits, treat it as the device's MAC. Restricted to homeassistant_*
    /// domains — other integrations put non-MAC ids (USB serials, cloud ids) in this slot.
    /// Note the radio EUI never appears on the LAN (it's a Zigbee/Thread radio, not an Ethernet
    /// NIC), so this buys OUI vendor resolution and a stable MAC identity, not ARP correlation.
    /// </summary>
    private static string? MacFromIdentifiers(HaDevice device)
    {
        foreach ((string domain, string value) in device.Identifiers)
        {
            if (domain.StartsWith("homeassistant_", StringComparison.OrdinalIgnoreCase)
                && value.Length == 12
                && value.All(Uri.IsHexDigit))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Pulls every bare device UUID out of this device's UPnP-flavored values — both
    /// <c>connections</c> entries of type "upnp" (HA's own hard connection type,
    /// <c>dr.CONNECTION_UPNP</c>, value shape <c>"uuid:xxxx-..."</c>) and <c>identifiers</c>
    /// entries on the "upnp" domain (integration-assigned, often the fuller USN shape
    /// <c>"uuid:xxxx-...::urn:schemas-upnp-org:device:..."</c>). A device can carry more than
    /// one distinct, valid UUID here — e.g. a router commonly advertises separate root UPnP
    /// devices (IGD and WPS/Basic profiles) with different UUIDs — so every one that parses is
    /// kept and each is registered as its own <see cref="FingerprintType.Uuid" /> fingerprint,
    /// matching whatever a network scanner's SSDP probe extracts from the same device's USN
    /// header (see NetworkDiscoveryCollector.EmitSsdpUuid). Order-preserving, de-duplicated.
    /// </summary>
    private static List<string> ExtractUpnpUuids(HaDevice device)
    {
        List<string> uuids = [];
        foreach ((string type, string value) in device.Connections)
        {
            TryAddUpnpUuid(uuids, type, value);
        }

        foreach ((string domain, string value) in device.Identifiers)
        {
            TryAddUpnpUuid(uuids, domain, value);
        }

        return uuids;
    }

    private static void TryAddUpnpUuid(List<string> uuids, string domainOrType, string value)
    {
        if (!domainOrType.Equals("upnp", StringComparison.OrdinalIgnoreCase)
         || !value.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string uuidPart = value[5..];
        int colonColon = uuidPart.IndexOf("::", StringComparison.Ordinal);
        if (colonColon >= 0)
        {
            uuidPart = uuidPart[..colonColon];
        }

        uuidPart = uuidPart.Trim();
        if (Guid.TryParse(uuidPart, out _) && !uuids.Contains(uuidPart, StringComparer.OrdinalIgnoreCase))
        {
            uuids.Add(uuidPart);
        }
    }

    /// <summary>Resolves the Core Long-Lived Access Token from the target's credential.</summary>
    private static string? ResolveCoreToken(TargetCredentials? creds) =>
        creds is ApiTokenCredentials { Token: { Length: > 0 } t } ? t : null;

    private static Uri BuildWebSocketUri(string baseUrl)
    {
        Uri httpUri = new(baseUrl);
        string scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new UriBuilder(httpUri) { Scheme = scheme, Path = "/api/websocket" }.Uri;
    }

    private async Task<IReadOnlyList<T>?> TryRunAsync<T>(
        Func<Task<IReadOnlyList<T>>> call,
        string command,
        string baseUrl
    )
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HomeAssistantCollectorLog.CommandFailed(_logger, ex, command, baseUrl);
            return null;
        }
    }

    private async Task<JsonElement?> GetDataAsync(
        string baseUrl,
        string path,
        string token,
        CancellationToken ct
    )
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, $"{baseUrl}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            JsonElement root = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
            if (root.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (root.TryGetProperty("data", out JsonElement data))
            {
                return data;
            }

            return null;
        }
        catch (Exception ex)
        {
            HomeAssistantCollectorLog.RequestFailed(_logger, path, ex);
            return null;
        }
    }
}

internal static partial class HomeAssistantCollectorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "No token available for {BaseUrl}.")]
    internal static partial void NoToken(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Home Assistant request to {Path} failed.")]
    internal static partial void RequestFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Home Assistant WebSocket at {BaseUrl}.")]
    internal static partial void ConnectFailed(ILogger logger, Exception ex, string baseUrl);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Home Assistant rejected the token for {BaseUrl} (auth_invalid) — check the Long-Lived Access Token."
    )]
    internal static partial void AuthInvalid(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Home Assistant command '{Command}' failed for {BaseUrl}.")]
    internal static partial void CommandFailed(ILogger logger, Exception ex, string command, string baseUrl);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Home Assistant device {DeviceId} at {BaseUrl} has no MAC and no allow-listed "
                + "identifier domain; skipping."
    )]
    internal static partial void NoIdentity(ILogger logger, string deviceId, string baseUrl);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Home Assistant device {DeviceId} at {BaseUrl} is a service entry (integration/add-on "
                + "bookkeeping, not hardware); skipping."
    )]
    internal static partial void ServiceEntry(ILogger logger, string deviceId, string baseUrl);
}