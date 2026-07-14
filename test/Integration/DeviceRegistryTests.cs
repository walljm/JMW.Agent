using JMW.Discovery.Core;
using JMW.Discovery.Server;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for DeviceRegistry against a real Postgres instance.
/// Each test truncates the relevant tables to ensure isolation.
/// </summary>
[Collection("Integration")]
public sealed class DeviceRegistryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public DeviceRegistryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private DeviceRegistry _registry = null!;

    public Task InitializeAsync()
    {
        _registry = new DeviceRegistry(_fixture.DataSource);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync("device_aliases", "device_fingerprints", "devices");
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NewFingerprint_CreatesDevice()
    {
        List<Fingerprint> fps = [new(FingerprintType.Mac, "001122334455")];

        (string deviceId, bool isNew) = await _registry.ResolveAsync(fps, source: "test");

        Assert.True(isNew);
        Assert.False(string.IsNullOrEmpty(deviceId));

        // Devices are stored as UUIDs; cast to text for the WHERE clause.
        long count = await _fixture.CountAsync("devices", $"device_id::text = '{deviceId}'");
        Assert.Equal(1, count);

        long fpCount = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id::text = '{deviceId}' AND fp_type = 'mac' AND fp_value = '001122334455'"
        );
        Assert.Equal(1, fpCount);
    }

    [Fact]
    public async Task ResolveAsync_SameFingerprint_ReturnsSameDevice()
    {
        List<Fingerprint> fps = [new(FingerprintType.Mac, "001122334455")];

        (string firstId, bool firstIsNew) = await _registry.ResolveAsync(fps, source: "test");
        (string secondId, bool secondIsNew) = await _registry.ResolveAsync(fps, source: "test");

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task ResolveAsync_MultipleFingerprints_AllStoredOnCreate()
    {
        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "001122334466"),
            new(FingerprintType.ChassisSerial, "cisco:ftx1234abcd"),
            new(FingerprintType.DiskSerial, "apple:0ba020cac2882e30"),
        ];

        (string deviceId, bool isNew) = await _registry.ResolveAsync(fps, source: "test");

        Assert.True(isNew);

        long macCount = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id::text = '{deviceId}' AND fp_type = 'mac'"
        );
        long serialCount = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id::text = '{deviceId}' AND fp_type = 'chassis-serial'"
        );
        long diskSerialCount = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id::text = '{deviceId}' AND fp_type = 'disk-serial'"
        );
        Assert.Equal(1, macCount);
        Assert.Equal(1, serialCount);
        Assert.Equal(1, diskSerialCount);
    }

    [Fact]
    public async Task ResolveAsync_ManagementStatus_PersistedOnDevice()
    {
        List<Fingerprint> fps = [new(FingerprintType.Mac, "001122334477")];

        (string deviceId, bool _) = await _registry.ResolveAsync(
            fps,
            source: "test",
            managementStatus: "discovered"
        );

        long count = await _fixture.CountAsync(
            "devices",
            $"device_id::text = '{deviceId}' AND management_status = 'discovered'"
        );
        Assert.Equal(1, count);
    }

    // ── Merge ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_TwoMatchingDevices_MergesOldestSurvives()
    {
        // Create two separate devices with different MACs.
        Guid olderId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid newerId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(olderId, FingerprintType.Mac, "001122334488");
        await _fixture.InsertFingerprintAsync(newerId, FingerprintType.Mac, "001122334499");

        // Resolve with both MACs — triggers auto-merge.
        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "001122334488"),
            new(FingerprintType.Mac, "001122334499"),
        ];

        (string survivorId, bool _) = await _registry.ResolveAsync(fps, source: "merge-test");

        // Older device survives.
        Assert.Equal(olderId.ToString(), survivorId);

        // Newer device should have an alias pointing to the survivor.
        long aliasCount = await _fixture.CountAsync(
            "device_aliases",
            $"alias_device_id = '{newerId}' AND survivor_device_id = '{olderId}'"
        );
        Assert.Equal(1, aliasCount);

        // Both MACs now on the survivor.
        long fpCount = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id::text = '{olderId}' AND fp_type = 'mac'"
        );
        Assert.Equal(2, fpCount);
    }

    // ── Alias resolution ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAliasAsync_NonAliasDevice_ReturnsOriginalId()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");
        string result = await _registry.ResolveAliasAsync(deviceId.ToString());
        Assert.Equal(deviceId.ToString(), result);
    }

    [Fact]
    public async Task ResolveAliasAsync_AliasedDevice_ReturnsSurvivorId()
    {
        Guid survivorId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid aliasId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(survivorId, FingerprintType.Mac, "0011223344aa");
        await _fixture.InsertFingerprintAsync(aliasId, FingerprintType.Mac, "0011223344bb");

        // Force a merge to create the alias relationship.
        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "0011223344aa"),
            new(FingerprintType.Mac, "0011223344bb"),
        ];
        await _registry.ResolveAsync(fps, source: "alias-test");

        string resolved = await _registry.ResolveAliasAsync(aliasId.ToString());
        Assert.Equal(survivorId.ToString(), resolved);
    }

    // ── Invalid fingerprints ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_AllInvalidFingerprints_ThrowsArgumentException()
    {
        // Broadcast MAC is invalid.
        List<Fingerprint> fps = [new(FingerprintType.Mac, "ffffffffffff")];

        await Assert.ThrowsAsync<ArgumentException>(() => _registry.ResolveAsync(fps, source: "test")
        );
    }
}