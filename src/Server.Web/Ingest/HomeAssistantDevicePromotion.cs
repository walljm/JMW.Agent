using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis.Normalizers;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Resolves and promotes Home Assistant device-registry entries into their own Device[]
/// rows, inline during ingest — every fact about a given HaDevice[] entry (mac, uuid,
/// identifiers, manufacturer, model, name) arrives from one collector in one cycle, so
/// there's no cross-collector/cross-cycle merge to wait for the way ARP/DHCP/scanner
/// discovery needs (see DiscoveryMaterializer.RelevantTables and
/// docs/plans/ha-inline-discovery.md). Called by FactsEndpoint right after ingesting a
/// home-assistant service batch's facts, off the same in-memory list — no projection
/// write-then-reread.
/// </summary>
public static class HomeAssistantDevicePromotion
{
    public static async Task PromoteAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Fact> serviceFacts,
        ILogger logger,
        CancellationToken ct
    )
    {
        foreach (HaDeviceEntry entry in GroupByHaDevice(serviceFacts))
        {
            List<Fingerprint> fps = [];
            if (NullIfBlank(entry.Identifiers) is { } id)
            {
                fps.Add(new Fingerprint(FingerprintType.HaIdentifiers, id));
            }

            if (NullIfBlank(entry.Mac) is { } mac)
            {
                fps.Add(new Fingerprint(FingerprintType.Mac, mac));
            }

            if (NullIfBlank(entry.UpnpUuid) is { } uuid)
            {
                fps.Add(new Fingerprint(FingerprintType.Uuid, uuid));
            }

            if (fps.Count == 0)
            {
                continue;
            }

            string deviceId;
            try
            {
                (deviceId, bool _) = await DeviceRegistry.ResolveWithConnectionAsync(
                    conn,
                    fps,
                    source: "home-assistant",
                    managementStatus: "discovered",
                    ct: ct
                );
            }
            catch (ArgumentException)
            {
                // Every fingerprint was unusable (e.g. a MAC that normalizes away). The raw
                // facts are already in facts_history/proj_service_ha_devices; mint no device.
                HomeAssistantDevicePromotionLog.MacNormalizationFailed(logger);
                continue;
            }

            // This promotion writes straight to the resolved device's hardware/summary rows via
            // direct SQL upsert, entirely outside the Fact/AnalysisEngine pipeline — so it must
            // apply the same vendor/model normalization the pipeline would apply itself, or an
            // HA-discovered device's manufacturer/model would reach reporting completely raw.
            string? cleanManufacturer = NormalizeManufacturer(NullIfBlank(entry.Manufacturer));
            string? cleanModel = NormalizeModel(NullIfBlank(entry.Model));
            if (cleanManufacturer != null || cleanModel != null)
            {
                await conn.UpsertDeviceHardwareAsync(deviceId, cleanManufacturer, cleanModel, serial: null, ct)
                    .ExecuteAsync(ct);
            }

            if (cleanManufacturer != null)
            {
                await conn.UpsertDeviceSummaryAsync(deviceId, cleanManufacturer, kind: null, ct).ExecuteAsync(ct);
            }

            // entry.Name is Home Assistant's user-configured registry display name (e.g. "Kitchen
            // Light") — never a real OS hostname (these are smart-home entities, not hosts) — so
            // it's promoted as friendly_name only.
            if (NullIfBlank(entry.Name) is { } cleanName)
            {
                await conn.UpsertDeviceSystemAsync(deviceId, hostname: null, cleanName, lastSeenIp: null, osFamily: null, ct)
                    .ExecuteAsync(ct);
            }
        }
    }

    /// <summary>
    /// Groups a home-assistant service batch's rewritten facts by their HaDevice[] list-dimension
    /// key, using Fact.ParseId() (the documented way to pull a key VALUE, not just its name, back
    /// out of a fact id — see Fact.cs). Facts with no HaDevice segment (the instance-level
    /// Supervisor/Core/OS/AddOn facts, the self-referential ServiceId fact) are not part of any
    /// entry and are skipped.
    /// </summary>
    private static Dictionary<string, HaDeviceEntry>.ValueCollection GroupByHaDevice(IReadOnlyList<Fact> serviceFacts)
    {
        Dictionary<string, HaDeviceEntry> entries = new(StringComparer.Ordinal);

        foreach (Fact fact in serviceFacts)
        {
            string? haDeviceKey = null;
            foreach (FactSegment segment in fact.ParseId())
            {
                if (segment.Name == "HaDevice")
                {
                    haDeviceKey = segment.Key;
                    break;
                }
            }

            if (haDeviceKey is null)
            {
                continue;
            }

            if (!entries.TryGetValue(haDeviceKey, out HaDeviceEntry? entry))
            {
                entry = new HaDeviceEntry();
                entries[haDeviceKey] = entry;
            }

            switch (fact.Attribute)
            {
                case "Mac":
                    entry.Mac = fact.Value.AsString();
                    break;
                case "UpnpUuid":
                    entry.UpnpUuid = fact.Value.AsString();
                    break;
                case "Identifiers":
                    entry.Identifiers = fact.Value.AsString();
                    break;
                case "Manufacturer":
                    entry.Manufacturer = fact.Value.AsString();
                    break;
                case "Model":
                    entry.Model = fact.Value.AsString();
                    break;
                case "Name":
                    entry.Name = fact.Value.AsString();
                    break;
            }
        }

        return entries.Values;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private static readonly VendorNormalizer Vendor = new();
    private static readonly ModelNormalizer Model = new([]);

    private static string? NormalizeManufacturer(string? s) =>
        s is null ? null : Vendor.Normalize(FactValue.FromString(s))?.AsString();

    private static string? NormalizeModel(string? s) =>
        s is null ? null : Model.Normalize(FactValue.FromString(s))?.AsString();

    private sealed class HaDeviceEntry
    {
        public string? Mac;
        public string? UpnpUuid;
        public string? Identifiers;
        public string? Manufacturer;
        public string? Model;
        public string? Name;
    }
}

internal static partial class HomeAssistantDevicePromotionLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped HA device: MAC normalization failed.")]
    public static partial void MacNormalizationFailed(ILogger logger);
}