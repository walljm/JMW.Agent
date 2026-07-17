using JMW.Discovery.Core;
using JMW.Discovery.Server;
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

    public HomeAssistantDevicePromotionTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync(
            "proj_systems",
            "proj_hardware",
            "proj_devices",
            "proj_device_arp",
            "device_aliases",
            "device_fingerprints",
            "devices"
        );
    }

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
        await HomeAssistantDevicePromotion.PromoteAsync(conn, facts, NullLogger.Instance, CancellationToken.None);

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
        await HomeAssistantDevicePromotion.PromoteAsync(conn, facts, NullLogger.Instance, CancellationToken.None);

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
        await HomeAssistantDevicePromotion.PromoteAsync(conn, facts, NullLogger.Instance, CancellationToken.None);

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
        await HomeAssistantDevicePromotion.PromoteAsync(conn, facts, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '001122334499'")
        );
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