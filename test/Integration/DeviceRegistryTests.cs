using JMW.Discovery.Core;
using JMW.Discovery.Server;

using Npgsql;

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
        await _fixture.TruncateAsync(
            "device_aliases",
            "device_fingerprints",
            "devices",
            "proj_interfaces",
            "materialization_facts",
            "change_events"
        );
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
    public async Task ResolveAsync_ReResolve_BumpsLastSeen_ForLivenessTouch()
    {
        // The agent's fingerprints-only "liveness touch" (a batch element with zero changed facts)
        // re-resolves an existing device so device_fingerprints.last_seen advances — the recency
        // signal visible_devices keys on. This guards the server-side contract that touch relies on:
        // re-resolution keeps a static device fresh and mints no duplicate.
        // Globally-administered MAC (first-octet low nibble has multicast/local bits clear) so it
        // survives NormalizeMac.
        List<Fingerprint> fps = [new(FingerprintType.Mac, "001122aabbcc")];
        (string id, _) = await _registry.ResolveAsync(fps, source: "test");

        // Age the sighting well past a typical window.
        await ExecAsync(
            "UPDATE device_fingerprints SET last_seen = now() - interval '48 hours' WHERE device_id::text = @id",
            ("id", id)
        );
        Assert.True(await LastSeenAsync(id) < DateTime.UtcNow - TimeSpan.FromHours(47));

        // Re-resolve with the same fingerprints — exactly what a liveness touch triggers server-side.
        (string id2, bool isNew2) = await _registry.ResolveAsync(fps, source: "test");

        Assert.Equal(id, id2);
        Assert.False(isNew2);
        Assert.True(await LastSeenAsync(id) > DateTime.UtcNow - TimeSpan.FromMinutes(5));
        Assert.Equal(1, await _fixture.CountAsync("devices", $"device_id::text = '{id}'"));
    }

    private async Task ExecAsync(string sql, params (string, object?)[] ps)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, object? val) in ps)
        {
            cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<DateTime> LastSeenAsync(string deviceId)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT max(last_seen) FROM device_fingerprints WHERE device_id::text = @id",
            conn
        );
        cmd.Parameters.AddWithValue("id", deviceId);
        object v = await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("no rows");
        return v is DateTimeOffset dto ? dto.UtcDateTime : ((DateTime)v).ToUniversalTime();
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

    [Fact]
    public async Task ResolveAsync_ThreeWayMerge_CollidingInterfaceRows_KeepsFreshestOnly()
    {
        // Three devices merge in one shot (all share overlapping fingerprints via a single
        // incoming batch). Two of the losers each independently tracked an "eth0" interface
        // row for what turns out to be the same physical device — the survivor has neither.
        // A batched (non-sequential) repoint would try to move both losers' eth0 rows onto
        // the survivor in one UPDATE and hit a primary-key violation.
        Guid survivorId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-3));
        Guid loserAId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid loserBId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(survivorId, FingerprintType.Mac, "00aabbccdd01");
        await _fixture.InsertFingerprintAsync(loserAId, FingerprintType.Mac, "00aabbccdd02");
        await _fixture.InsertFingerprintAsync(loserBId, FingerprintType.Mac, "00aabbccdd03");

        await InsertProjInterfaceAsync(loserAId, "eth0", name: "stale-eth0", updatedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await InsertProjInterfaceAsync(loserBId, "eth0", name: "fresh-eth0", updatedAt: DateTimeOffset.UtcNow);

        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "00aabbccdd01"),
            new(FingerprintType.Mac, "00aabbccdd02"),
            new(FingerprintType.Mac, "00aabbccdd03"),
        ];

        (string resolvedSurvivor, bool _) = await _registry.ResolveAsync(fps, source: "3way-merge-test");
        Assert.Equal(survivorId.ToString(), resolvedSurvivor);

        long ifaceRowCount = await _fixture.CountAsync("proj_interfaces", $"device = '{survivorId}'");
        Assert.Equal(1, ifaceRowCount);

        long freshRowCount = await _fixture.CountAsync(
            "proj_interfaces",
            $"device = '{survivorId}' AND name = 'fresh-eth0'"
        );
        Assert.Equal(1, freshRowCount);
    }

    private async Task InsertProjInterfaceAsync(Guid device, string interfaceKey, string name, DateTimeOffset updatedAt)
    {
        await using Npgsql.NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "INSERT INTO proj_interfaces (device, interface, name, updated_at) VALUES (@d, @i, @n, @u)",
            conn
        );
        cmd.Parameters.AddWithValue("d", device.ToString());
        cmd.Parameters.AddWithValue("i", interfaceKey);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("u", updatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ResolveAsync_TwoMatchingDevices_MergesMaterializationFacts_NonCollidingAndColliding()
    {
        // survivor already has SshHostKey (no OnvifSerial); loser has OnvifSerial (moves outright)
        // and a colliding SshHostKey that is FRESHER than the survivor's — the fresher side must win.
        Guid survivorId = await _fixture.InsertDeviceAsync("discovered", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid loserId = await _fixture.InsertDeviceAsync("discovered", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(survivorId, FingerprintType.Mac, "00ccddeeff01");
        await _fixture.InsertFingerprintAsync(loserId, FingerprintType.ChassisSerial, "bare:onvif-merge-1");

        await InsertMaterializationFactAsync(
            survivorId,
            "10.0.0.1",
            "Device[].Discovered[].SshHostKey",
            "stale-key",
            DateTimeOffset.UtcNow.AddHours(-2)
        );
        await InsertMaterializationFactAsync(
            loserId,
            "10.0.0.1",
            "Device[].Discovered[].SshHostKey",
            "fresh-key",
            DateTimeOffset.UtcNow
        );
        await InsertMaterializationFactAsync(
            loserId,
            "10.0.0.2",
            "Device[].Discovered[].OnvifSerial",
            "onvif-serial-1",
            DateTimeOffset.UtcNow
        );

        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "00ccddeeff01"),
            new(FingerprintType.ChassisSerial, "bare:onvif-merge-1"),
        ];

        (string resolvedSurvivor, bool _) = await _registry.ResolveAsync(fps, source: "identity-facts-merge-test");
        Assert.Equal(survivorId.ToString(), resolvedSurvivor);

        // Non-colliding row moved over outright.
        long onvifCount = await _fixture.CountAsync(
            "materialization_facts",
            $"device = '{survivorId}' AND entity_key = '10.0.0.2' " +
            "AND attribute_path = 'Device[].Discovered[].OnvifSerial' AND value = 'onvif-serial-1'"
        );
        Assert.Equal(1, onvifCount);

        // Colliding row: fresher (loser's) value wins, and only one row remains.
        long sshHostKeyCount = await _fixture.CountAsync(
            "materialization_facts",
            $"device = '{survivorId}' AND entity_key = '10.0.0.1' " +
            "AND attribute_path = 'Device[].Discovered[].SshHostKey'"
        );
        Assert.Equal(1, sshHostKeyCount);

        long freshWinsCount = await _fixture.CountAsync(
            "materialization_facts",
            $"device = '{survivorId}' AND entity_key = '10.0.0.1' " +
            "AND attribute_path = 'Device[].Discovered[].SshHostKey' AND value = 'fresh-key'"
        );
        Assert.Equal(1, freshWinsCount);

        // Nothing left behind under the loser id.
        long loserRowCount = await _fixture.CountAsync("materialization_facts", $"device = '{loserId}'");
        Assert.Equal(0, loserRowCount);
    }

    private async Task InsertMaterializationFactAsync(
        Guid device,
        string entityKey,
        string attributePath,
        string value,
        DateTimeOffset updatedAt
    )
    {
        await using Npgsql.NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            """
            INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
            VALUES (@d, @e, @p, @v, @u)
            """,
            conn
        );
        cmd.Parameters.AddWithValue("d", device.ToString());
        cmd.Parameters.AddWithValue("e", entityKey);
        cmd.Parameters.AddWithValue("p", attributePath);
        cmd.Parameters.AddWithValue("v", value);
        cmd.Parameters.AddWithValue("u", updatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ResolveAsync_TwoMatchingDevices_WritesMergedChangeEvent()
    {
        Guid olderId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid newerId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(olderId, FingerprintType.Mac, "00aabbcc1010");
        await _fixture.InsertFingerprintAsync(newerId, FingerprintType.Mac, "00aabbcc2020");

        List<Fingerprint> fps =
        [
            new(FingerprintType.Mac, "00aabbcc1010"),
            new(FingerprintType.Mac, "00aabbcc2020"),
        ];

        (string survivorId, bool _) = await _registry.ResolveAsync(fps, source: "merge-event-test");

        // Ingest-time auto-merge is the dominant merge path (no operator involved) — it must
        // record a "merged" change_event just like manual merge does, or Recent Activity/Device
        // History silently omit the majority of real-world merges.
        long count = await _fixture.CountAsync(
            "change_events",
            $"event_type = 'merged' AND entity_kind = 'device' AND entity_id = '{survivorId}'"
        );
        Assert.Equal(1, count);
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

    // ── Same-cycle IP correlation (ADR-002 amendment, 2026-07-23) ──────────────

    [Fact]
    public async Task MergeCorrelatedByIpAsync_TwoDevices_MergesOldestSurvives()
    {
        Guid olderId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid newerId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        // Distinct fingerprint types — these two would never merge via ordinary fingerprint
        // overlap (FindMatchingDeviceIdsAsync); only the same-cycle IP signal links them.
        await _fixture.InsertFingerprintAsync(olderId, FingerprintType.Mac, "00aabbcc3001");
        await _fixture.InsertFingerprintAsync(newerId, FingerprintType.SshHostKey, "sha256:same-cycle-test");

        string? survivorId = await _registry.MergeCorrelatedByIpAsync([olderId.ToString(), newerId.ToString()]);

        Assert.Equal(olderId.ToString(), survivorId);

        long aliasCount = await _fixture.CountAsync(
            "device_aliases",
            $"alias_device_id = '{newerId}' AND survivor_device_id = '{olderId}'"
        );
        Assert.Equal(1, aliasCount);

        long fpCount = await _fixture.CountAsync("device_fingerprints", $"device_id::text = '{olderId}'");
        Assert.Equal(2, fpCount);

        long changeEventCount = await _fixture.CountAsync(
            "change_events",
            $"event_type = 'merged' AND entity_kind = 'device' AND entity_id = '{olderId}'"
        );
        Assert.Equal(1, changeEventCount);
    }

    [Fact]
    public async Task MergeCorrelatedByIpAsync_SingleDevice_NoOp()
    {
        Guid id = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(id, FingerprintType.Mac, "00aabbcc3002");

        string? result = await _registry.MergeCorrelatedByIpAsync([id.ToString()]);

        Assert.Equal(id.ToString(), result);
        Assert.Equal(0, await _fixture.CountAsync("device_aliases", $"alias_device_id = '{id}'"));
    }

    [Fact]
    public async Task MergeCorrelatedByIpAsync_AlreadyAliasedId_ResolvesThroughAliasFirst()
    {
        // Simulates a device id merged away by a DIFFERENT IP group earlier in the same request —
        // MergeCorrelatedByIpAsync must resolve it to its current survivor before merging
        // further, rather than operating on a since-deleted devices row.
        Guid survivorId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-3));
        Guid mergedAwayId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-2));
        Guid thirdId = await _fixture.InsertDeviceAsync("managed", createdAt: DateTimeOffset.UtcNow.AddHours(-1));

        await _fixture.InsertFingerprintAsync(survivorId, FingerprintType.Mac, "00aabbcc3003");
        await _fixture.InsertFingerprintAsync(mergedAwayId, FingerprintType.Mac, "00aabbcc3004");
        await _fixture.InsertFingerprintAsync(thirdId, FingerprintType.SshHostKey, "sha256:alias-chain-test");

        // Pre-merge mergedAwayId into survivorId (a prior IP group in the same pass).
        await _registry.MergeCorrelatedByIpAsync([survivorId.ToString(), mergedAwayId.ToString()]);

        // Now merge the (already-merged-away) mergedAwayId's id together with thirdId.
        string? finalSurvivor = await _registry.MergeCorrelatedByIpAsync(
            [mergedAwayId.ToString(), thirdId.ToString()]
        );

        Assert.Equal(survivorId.ToString(), finalSurvivor);
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_aliases",
                $"alias_device_id = '{thirdId}' AND survivor_device_id = '{survivorId}'"
            )
        );
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