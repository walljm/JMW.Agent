using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
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
        FactIngestPipeline pipeline,
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

            // §5 IP-join: an otherwise MAC-less device (mobile_app/google_home/etc. — the
            // collector's gate now admits these specifically for this) recovers a real MAC by
            // joining its self-reported Wi-Fi IP against this agent's own LAN data. This
            // usually merges the entry onto a device some OTHER collector already tracks by
            // MAC (a phone/speaker seen via ARP or mDNS), rather than minting a new one.
            if (fps.Count == 0 && NullIfBlank(entry.WifiIp) is { } wifiIp)
            {
                string? recoveredMac = await TryRecoverMacByIpAsync(conn, wifiIp, entry.AgentId, entry.Manufacturer, ct);
                if (recoveredMac is { } ipRecoveredMac)
                {
                    fps.Add(new Fingerprint(FingerprintType.Mac, ipRecoveredMac));
                    HomeAssistantDevicePromotionLog.MacRecoveredByIp(logger, wifiIp, ipRecoveredMac);
                }
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
            // it's promoted as friendly_name only. WifiIp rides the same upsert's existing
            // lastSeenIp param (§6) — last-sighting-wins, same as every other last_seen_ip writer.
            string? cleanName = NullIfBlank(entry.Name);
            string? cleanWifiIp = NullIfBlank(entry.WifiIp);
            if (cleanName != null || cleanWifiIp != null)
            {
                await conn.UpsertDeviceSystemAsync(
                        deviceId,
                        hostname: null,
                        cleanName,
                        lastSeenIp: cleanWifiIp,
                        osFamily: null,
                        ct
                    )
                    .ExecuteAsync(ct);
            }

            // Firmware/software version — device-registry native or the update.*_firmware
            // fallback (HomeAssistantCollector.AddHealthFacts). Fill-only, own proj_hardware
            // column (§6); see docs/plans/architecture-identity-facts.md §10.7 for why this
            // stays a direct SQL upsert rather than routing through the real ingest path.
            if (NullIfBlank(entry.SwVersion) is { } cleanSwVersion)
            {
                await conn.UpsertDeviceFirmwareAsync(deviceId, cleanSwVersion, ct).ExecuteAsync(ct);
            }

            // Battery percent has no projection column at all (proj_batteries was dropped —
            // migration 0031 — in favor of the device-detail "Battery" fact view read straight
            // from facts_history), so there is no LWW-projection-column precedence risk here
            // the way there was for vendor (§10.7) — routing this one fact through the real
            // ingest pipeline is correct, not just convenient, and gets it proper provenance.
            if (entry.BatteryPercent is { } batteryPercent)
            {
                Fact batteryFact = Fact.Create(
                    FactPaths.BatteryChargePercent,
                    [deviceId],
                    (double)batteryPercent
                ) with
                {
                    AgentId = entry.AgentId,
                };
                await pipeline.IngestAsync([batteryFact], ct);
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
                case "SwVersion":
                    entry.SwVersion = fact.Value.AsString();
                    break;
                case "WifiIp":
                    entry.WifiIp = fact.Value.AsString();
                    break;
                case "BatteryPercent":
                    entry.BatteryPercent = fact.Value.AsLong();
                    break;
            }

            entry.AgentId ??= fact.AgentId;
        }

        return entries.Values;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// The IP-join itself (docs/plans/ha-device-enrichment.md §5): looks up every MAC this
    /// agent's own LAN has attested for <paramref name="ip" /> (already scoped to
    /// <paramref name="agentId" /> — see GetKnownMacsWithVendorForIp.sql) and accepts a
    /// candidate only when it is unambiguous. With a known manufacturer, "unambiguous" means
    /// exactly one candidate whose IEEE OUI vendor matches (normalized, same VendorNormalizer
    /// this file already uses for promoted values) — the second independent signal beyond the
    /// bare IP match. Without a known manufacturer there is no second signal available, so it
    /// falls back to requiring exactly one candidate at the IP at all, matching the strictness
    /// the existing Google Wifi obscured-MAC-by-OUI path already applies (ObscuredMac.Pick).
    /// Never guesses: an empty or still-ambiguous result returns null, not a best-effort pick.
    /// </summary>
    private static async Task<string?> TryRecoverMacByIpAsync(
        NpgsqlConnection conn,
        string ip,
        Guid? agentId,
        string? manufacturer,
        CancellationToken ct
    )
    {
        // Mac is nullable on the query's own result shape (the validator requires this — the
        // UNION+WHERE that guarantees non-null doesn't get proven through by schema
        // introspection) but is never actually null once "length(norm.mac) = 12" has passed.
        List<(string Mac, string? Vendor)> candidates = [];
        await foreach ((string? mac, string? vendor) in conn.GetKnownMacsWithVendorForIpAsync(ip, agentId, ct)
                     .WithCancellation(ct))
        {
            if (mac is { Length: 12 })
            {
                candidates.Add((mac, vendor));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        string? cleanManufacturer = NormalizeManufacturer(NullIfBlank(manufacturer));
        if (cleanManufacturer is null)
        {
            return candidates.Count == 1 ? candidates[0].Mac : null;
        }

        List<string> vendorMatched = candidates
            .Where(c => VendorsMatch(cleanManufacturer, NormalizeManufacturer(NullIfBlank(c.Vendor))))
            .Select(c => c.Mac)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return vendorMatched.Count == 1 ? vendorMatched[0] : null;
    }

    private static bool VendorsMatch(string cleanManufacturer, string? cleanOuiVendor) =>
        cleanOuiVendor is not null
        && (cleanManufacturer.Equals(cleanOuiVendor, StringComparison.OrdinalIgnoreCase)
         || cleanManufacturer.Contains(cleanOuiVendor, StringComparison.OrdinalIgnoreCase)
         || cleanOuiVendor.Contains(cleanManufacturer, StringComparison.OrdinalIgnoreCase));

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
        public string? SwVersion;
        public string? WifiIp;
        public long? BatteryPercent;
        public Guid? AgentId;
    }
}

internal static partial class HomeAssistantDevicePromotionLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped HA device: MAC normalization failed.")]
    public static partial void MacNormalizationFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Recovered MAC {Mac} for MAC-less HA device by IP {Ip}.")]
    public static partial void MacRecoveredByIp(ILogger logger, string ip, string mac);
}