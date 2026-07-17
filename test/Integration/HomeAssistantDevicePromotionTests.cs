using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for HomeAssistantDevicePromotion: verifies that Home Assistant
/// device-registry facts resolve/promote into Device[] rows inline, straight from the
/// ingest batch's own in-memory facts — no proj_service_ha_devices reread. Replaces the
/// old DiscoveryMaterializer-based HA tests; see docs/plans/ha-inline-discovery.md.
/// </summary>
[Collection("Integration")]
public sealed class HomeAssistantDevicePromotionTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private FactIngestPipeline _pipeline = null!;

    public HomeAssistantDevicePromotionTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_fixture.DataSource);
        FactRepository repo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        ProjectionRouter router = new(_fixture.DataSource, projections);
        IncidentEvaluator incidents = new(_fixture.DataSource, IncidentTypeRegistry.CreateAll());
        _pipeline = new FactIngestPipeline(repo, router, AnalysisLibrary.CreateEngine(), incidents);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync(
            "proj_systems",
            "proj_hardware",
            "proj_devices",
            "proj_device_arp",
            "oui_entries",
            "device_aliases",
            "device_fingerprints",
            "devices",
            "facts_history"
        );
    }

    private async Task PromoteAsync(NpgsqlConnection conn, List<Fact> facts) =>
        await HomeAssistantDevicePromotion.PromoteAsync(conn, _pipeline, facts, NullLogger.Instance, CancellationToken.None);

    private static Fact HaFact(string service, string haDevice, string attribute, string value) =>
        Fact.Create($"Service[{service}].HomeAssistant.HaDevice[{haDevice}].{attribute}", value);

    [Fact]
    public async Task PromoteAsync_Mac_CreatesDeviceAndPromotesHardware()
    {
        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-hue-1", "Mac", "001122334477"),
            HaFact("svc-ha-1", "dev-hue-1", "Manufacturer", "Signify"),
            HaFact("svc-ha-1", "dev-hue-1", "Model", "Hue color lamp"),
            HaFact("svc-ha-1", "dev-hue-1", "Name", "Living Room Lamp"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '001122334477'")
        );

        string? deviceId = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122334477'"
        );
        Assert.NotNull(deviceId);
        Assert.Equal(
            "Signify",
            await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{deviceId}'")
        );
        Assert.Equal(
            "Hue color lamp",
            await ReadScalarAsync($"SELECT system_model FROM proj_hardware WHERE device = '{deviceId}'")
        );
        Assert.Equal("Signify", await ReadScalarAsync($"SELECT vendor FROM proj_devices WHERE device = '{deviceId}'"));
        // entry.Name is HA's registry display name, not a real OS hostname — promoted as
        // friendly_name only; hostname stays null (HA entities have no OS to report one).
        Assert.Null(await ReadScalarAsync($"SELECT hostname FROM proj_systems WHERE device = '{deviceId}'"));
        Assert.Equal(
            "Living Room Lamp",
            await ReadScalarAsync($"SELECT friendly_name FROM proj_systems WHERE device = '{deviceId}'")
        );
    }

    [Fact]
    public async Task PromoteAsync_NoMac_MintsDeviceOnHaIdentifiers()
    {
        // Most Zigbee/Z-Wave/Bluetooth devices behind a coordinator carry no MAC at all —
        // the ha-identifiers fingerprint is the only way to surface them as inventory.
        const string identifiers = "zha:00:11:22:33:44:55:66:77";
        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-zha-bulb", "Identifiers", identifiers),
            HaFact("svc-ha-1", "dev-zha-bulb", "Manufacturer", "IKEA"),
            HaFact("svc-ha-1", "dev-zha-bulb", "Model", "TRADFRI bulb"),
            HaFact("svc-ha-1", "dev-zha-bulb", "Name", "Bedroom Bulb"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", $"fp_type = 'ha-identifiers' AND fp_value = '{identifiers}'")
        );
    }

    [Fact]
    public async Task PromoteAsync_ExistingMacDevice_MergesOntoIt()
    {
        // The same physical device was already discovered via ARP by MAC; promotion must
        // merge onto that device rather than minting a second one.
        DiscoveryMaterializer materializer = new(
            _fixture.DataSource,
            new FactRepository(_fixture.DataSource, new MetricsRepository(_fixture.DataSource)),
            new ProjectionRouter(_fixture.DataSource, ProjectionLibrary.CreateAll(_fixture.DataSource)),
            NullLoggerFactory.Instance.CreateLogger<DiscoveryMaterializer>()
        );
        await InsertArpRowAsync("observer-device-1", "192.168.1.150", "001122334488", "eth0");
        await materializer.MaterializeAsync(CancellationToken.None);

        string? arpDeviceId = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122334488'"
        );
        Assert.NotNull(arpDeviceId);

        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-shelly-1", "Mac", "001122334488"),
            HaFact("svc-ha-1", "dev-shelly-1", "Identifiers", "shelly:abc123"),
            HaFact("svc-ha-1", "dev-shelly-1", "Manufacturer", "Shelly"),
            HaFact("svc-ha-1", "dev-shelly-1", "Model", "Shelly Plug S"),
            HaFact("svc-ha-1", "dev-shelly-1", "Name", "Office Plug"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        string? viaIdentifiers = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'ha-identifiers' AND fp_value = 'shelly:abc123'"
        );
        Assert.Equal(arpDeviceId, viaIdentifiers);
        Assert.Equal("Shelly", await ReadScalarAsync($"SELECT vendor FROM proj_devices WHERE device = '{arpDeviceId}'"));
    }

    [Fact]
    public async Task PromoteAsync_BadMac_SkipsEntryWithoutThrowing()
    {
        // A locally-administered/randomized MAC normalizes away. This entry must be
        // skipped, not throw and abort the rest of the batch (multiple entries in one
        // HA service batch are independent — see docs/plans/ha-inline-discovery.md).
        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-bad-mac", "Mac", "8a32a4377887"), // locally administered
            HaFact("svc-ha-1", "dev-good", "Mac", "001122334499"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '001122334499'")
        );
    }

    [Fact]
    public async Task PromoteAsync_SwVersionIpBattery_PromotesOntoDevice()
    {
        // docs/plans/ha-device-enrichment.md §6: firmware -> proj_hardware.firmware_version
        // (fill-only), WifiIp -> proj_systems.last_seen_ip (existing param, now wired), and
        // BatteryPercent -> the real ingest path (Device[].Battery.ChargePercent) since there
        // is no projection column for it (proj_batteries was dropped — migration 0031).
        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-tablet-1", "Mac", "001122335500"),
            HaFact("svc-ha-1", "dev-tablet-1", "SwVersion", "1.2.3"),
            HaFact("svc-ha-1", "dev-tablet-1", "WifiIp", "192.168.1.205"),
            HaFact("svc-ha-1", "dev-tablet-1", "BatteryPercent", 45L),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        string? deviceId = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122335500'"
        );
        Assert.NotNull(deviceId);
        Assert.Equal(
            "1.2.3",
            await ReadScalarAsync($"SELECT firmware_version FROM proj_hardware WHERE device = '{deviceId}'")
        );
        Assert.Equal(
            "192.168.1.205",
            await ReadScalarAsync($"SELECT last_seen_ip FROM proj_systems WHERE device = '{deviceId}'")
        );
        Assert.Equal(
            "45",
            await ReadScalarAsync(
                $"SELECT value_double::text FROM facts_history WHERE attribute_path = 'Device[].Battery.ChargePercent' AND key_values = '{{\"Device\":\"{deviceId}\"}}'"
            )
        );
    }

    [Fact]
    public async Task PromoteAsync_ExistingSwVersion_NeverOverwritten()
    {
        // Fill-only: a value already on the device (e.g. from a prior poll, or another
        // collector) must survive a later promotion carrying a different value.
        List<Fact> firstBatch =
        [
            HaFact("svc-ha-1", "dev-radio-1", "Mac", "001122335511"),
            HaFact("svc-ha-1", "dev-radio-1", "SwVersion", "1.0.0"),
        ];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, firstBatch);

        List<Fact> secondBatch =
        [
            HaFact("svc-ha-1", "dev-radio-1", "Mac", "001122335511"),
            HaFact("svc-ha-1", "dev-radio-1", "SwVersion", "2.0.0"),
        ];
        await PromoteAsync(conn, secondBatch);

        string? deviceId = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122335511'"
        );
        Assert.Equal(
            "1.0.0",
            await ReadScalarAsync($"SELECT firmware_version FROM proj_hardware WHERE device = '{deviceId}'")
        );
    }

    private static Fact HaFact(string service, string haDevice, string attribute, long value) =>
        Fact.Create($"Service[{service}].HomeAssistant.HaDevice[{haDevice}].{attribute}", value);

    [Fact]
    public async Task PromoteAsync_IpJoin_RecoversMacWhenVendorMatches()
    {
        // §5: a MAC-less mobile_app-style entry (no Mac/UpnpUuid/Identifiers) recovers a real
        // MAC by joining its self-reported Wi-Fi IP against this agent's own ARP data, cross-
        // checked against the OUI vendor for "001199" matching the HA-reported manufacturer.
        // MAC prefixes here (and below) use a globally-unique first octet (0x00) — a
        // locally-administered prefix (e.g. "aabbcc", whose first octet has bit 0x02 set)
        // normalizes away during resolve, same as PromoteAsync_BadMac_SkipsEntryWithoutThrowing.
        await InsertOuiEntryAsync("001199", "Google Inc.");
        await InsertArpRowAsync("observer-device-2", "192.168.1.50", "001199112233", "eth0");

        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-pixel-1", "WifiIp", "192.168.1.50"),
            HaFact("svc-ha-1", "dev-pixel-1", "Manufacturer", "Google"),
            HaFact("svc-ha-1", "dev-pixel-1", "Name", "Pixel Tablet"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '001199112233'")
        );
    }

    [Fact]
    public async Task PromoteAsync_IpJoin_AmbiguousCandidates_DoesNotRecover()
    {
        // Two different observers report two different MACs at the same IP, both with a
        // vendor matching the HA manufacturer — still ambiguous, so no fingerprint is
        // recovered from either. Never guess between corroborating candidates.
        await InsertOuiEntryAsync("002299", "Google Inc.");
        await InsertOuiEntryAsync("003399", "Google LLC");
        await InsertArpRowAsync("observer-device-3", "192.168.1.60", "002299998877", "eth0");
        await InsertArpRowAsync("observer-device-4", "192.168.1.60", "003399998877", "eth0");

        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-pixel-2", "WifiIp", "192.168.1.60"),
            HaFact("svc-ha-1", "dev-pixel-2", "Manufacturer", "Google"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                "fp_type = 'mac' AND fp_value IN ('002299998877', '003399998877')"
            )
        );
    }

    [Fact]
    public async Task PromoteAsync_IpJoin_NoManufacturer_RecoversOnlyWhenCandidateIsUnique()
    {
        // With no manufacturer to cross-check, fall back to requiring the IP resolve exactly
        // one candidate at all — the same strictness the Google Wifi obscured-MAC-by-OUI path
        // already applies (ObscuredMac.Pick).
        await InsertArpRowAsync("observer-device-5", "192.168.1.70", "004499aabbcc", "eth0");

        List<Fact> facts =
        [
            HaFact("svc-ha-1", "dev-unknown-1", "WifiIp", "192.168.1.70"),
        ];

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await PromoteAsync(conn, facts);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '004499aabbcc'")
        );
    }

    private async Task InsertOuiEntryAsync(string prefix, string vendor)
    {
        const string sql = """
            INSERT INTO oui_entries (prefix, bits, vendor)
            VALUES (@prefix, 24, @vendor)
            ON CONFLICT (prefix, bits) DO UPDATE SET vendor = EXCLUDED.vendor
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("vendor", vendor);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertArpRowAsync(string device, string ip, string mac, string iface)
    {
        const string sql = """
            INSERT INTO proj_device_arp (device, arp, mac, iface, state)
            VALUES (@device, @ip, @mac, @iface, 'reachable')
            ON CONFLICT (device, arp) DO UPDATE SET mac = EXCLUDED.mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", mac);
        cmd.Parameters.AddWithValue("iface", iface);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : result.ToString();
    }
}