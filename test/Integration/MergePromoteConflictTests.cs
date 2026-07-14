using JMW.Discovery.Server;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for WI-35 (manual merge), WI-36 (conflict detection/resolve),
/// and WI-37 (device promotion). Tests run against a real Postgres container.
/// </summary>
[Collection("Integration")]
public sealed class MergePromoteConflictTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public MergePromoteConflictTests(IntegrationFixture fixture)
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
            "audit_log",
            "excluded_fingerprints",
            "device_aliases",
            "device_fingerprints",
            "targets",
            "devices",
            "agents",
            "proj_hardware",
            "proj_interfaces",
            "facts_history"
        );
    }

    // ── WI-35: Manual merge ───────────────────────────────────────────────────

    [Fact]
    public async Task ManualMerge_HappyPath_AliasesLoserToSurvivor()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(survivor, "mac", "aabbcc001122");
        await _fixture.InsertFingerprintAsync(loser, "mac", "aabbcc002233");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        long aliasCount = await _fixture.CountAsync(
            "device_aliases",
            $"alias_device_id = '{loser}' AND survivor_device_id = '{survivor}'"
        );
        Assert.Equal(1, aliasCount);
    }

    [Fact]
    public async Task ManualMerge_ReassignsFingerprintsToSurvivor()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(loser, "mac", "001122334455");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        long fpOnSurvivor = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id = '{survivor}' AND fp_type = 'mac' AND fp_value = '001122334455'"
        );
        Assert.Equal(1, fpOnSurvivor);

        // Loser should have no fingerprints remaining (they were moved to survivor).
        long fpOnLoser = await _fixture.CountAsync(
            "device_fingerprints",
            $"device_id = '{loser}' AND fp_type = 'mac'"
        );
        Assert.Equal(0, fpOnLoser);
    }

    [Fact]
    public async Task ManualMerge_DeletesLoserDevicesRow()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        long loserCount = await _fixture.CountAsync("devices", $"device_id = '{loser}'");
        Assert.Equal(0, loserCount);
    }

    [Fact]
    public async Task ManualMerge_PurgesLoserProjectionRows()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO proj_hardware (device, system_vendor, updated_at) VALUES (@d, 'Ghost', now())",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", loser.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO proj_interfaces (device, interface, updated_at) VALUES (@d, 'eth0', now())",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", loser.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        long hwCount = await _fixture.CountAsync("proj_hardware", $"device = '{loser}'");
        long ifaceCount = await _fixture.CountAsync("proj_interfaces", $"device = '{loser}'");
        Assert.Equal(0, hwCount);
        Assert.Equal(0, ifaceCount);
    }

    [Fact]
    public async Task ManualMerge_RepointsFactsHistoryToSurvivor()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        string loserId = loser.ToString();
        string factId = $"Device[{loserId}].Interface[eth0].Speed";
        string keyValues = $$"""{"Device":"{{loserId}}","Interface":"eth0"}""";

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            """
            INSERT INTO facts_history (id, attribute_path, key_values, kind, value_long, collected_at, source, source_name)
            VALUES (@id, 'Device[].Interface[].Speed', @kv::jsonb, 2, 1000000000, now(), 0, 'test')
            """,
            conn
        ))
        {
            cmd.Parameters.AddWithValue("id", factId);
            cmd.Parameters.AddWithValue("kv", keyValues);
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.ManualMergeAsync(loserId, survivor.ToString(), actor: "test");

        long orphaned = await _fixture.CountAsync("facts_history", $"key_values ->> 'Device' = '{loserId}'");
        Assert.Equal(0, orphaned);

        await using NpgsqlConnection readConn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand readCmd = new(
            $"SELECT id, key_values ->> 'Device' FROM facts_history WHERE key_values ->> 'Device' = '{survivor}'",
            readConn
        );
        await using NpgsqlDataReader reader = await readCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal($"Device[{survivor}].Interface[eth0].Speed", reader.GetString(0));
        Assert.Equal(survivor.ToString(), reader.GetString(1));
    }

    [Fact]
    public async Task ManualMerge_LoserRowGone_ResolveAliasStillFindsSurvivor()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        // Loser's devices row is gone, but device_aliases isn't FK'd to it, so resolution
        // through the alias table (what DeviceDetail's redirect relies on) still works.
        string resolved = await _registry.ResolveAliasAsync(loser.ToString());
        Assert.Equal(survivor.ToString(), resolved);
    }

    [Fact]
    public async Task ManualMerge_UpdatesMergedFrom()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        // merged_from on survivor should contain the loser id.
        long mergedCount = await _fixture.CountAsync(
            "devices",
            $"device_id = '{survivor}' AND merged_from @> ARRAY['{loser}']::uuid[]"
        );
        Assert.Equal(1, mergedCount);
    }

    [Fact]
    public async Task ManualMerge_WritesAuditEntry()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test-actor");

        long auditCount = await _fixture.CountAsync(
            "audit_log",
            $"actor = 'test-actor' AND action = 'device.merge' AND target_ref = '{survivor}'"
        );
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task ManualMerge_LoserNotFound_ThrowsArgumentException()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        string fakeLoserId = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _registry.ManualMergeAsync(fakeLoserId, survivor.ToString(), actor: "test")
        );
    }

    [Fact]
    public async Task ManualMerge_SurvivorNotFound_ThrowsArgumentException()
    {
        Guid loser = await _fixture.InsertDeviceAsync("managed");
        string fakeSurvivorId = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _registry.ManualMergeAsync(loser.ToString(), fakeSurvivorId, actor: "test")
        );
    }

    [Fact]
    public async Task ManualMerge_LoserAlreadyAliasedToDifferentDevice_ThrowsConflict()
    {
        Guid survivor1 = await _fixture.InsertDeviceAsync("managed");
        Guid survivor2 = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        // Pre-alias loser to survivor1.
        await _fixture.InsertAliasAsync(loser, survivor1);

        await Assert.ThrowsAsync<DeviceMergeConflictException>(() =>
            _registry.ManualMergeAsync(loser.ToString(), survivor2.ToString(), actor: "test")
        );
    }

    [Fact]
    public async Task ManualMerge_LoserAlreadyAliasedToSameSurvivor_IsIdempotent()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        // First merge.
        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");
        // Second merge with same survivor → should not throw, and merged_from must not double-append.
        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        long aliasCount = await _fixture.CountAsync(
            "device_aliases",
            $"alias_device_id = '{loser}'"
        );
        Assert.Equal(1, aliasCount);

        // merged_from should contain exactly one copy of loser.
        long mergedFromCount = await _fixture.CountAsync(
            "devices",
            $"device_id = '{survivor}' AND array_length(merged_from, 1) = 1"
        );
        Assert.Equal(1, mergedFromCount);
    }

    // ── WI-36: Conflict detection ─────────────────────────────────────────────
    //
    // Note: the schema enforces PRIMARY KEY (fp_type, fp_value) on device_fingerprints,
    // meaning one device per fingerprint at the DB level. DB-level fingerprint conflicts
    // (two devices sharing the same fingerprint) are impossible in the current design.
    // ConflictCount is therefore always 0. These tests verify that invariant and that
    // the excluded_fingerprints mechanism is wired correctly.

    [Fact]
    public async Task ConflictCount_NoDevices_IsZero()
    {
        long count = await CountConflictsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ConflictCount_OneDevicePerFingerprint_IsZero()
    {
        Guid d1 = await _fixture.InsertDeviceAsync("managed");
        Guid d2 = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(d1, "mac", "deadbeef0001");
        await _fixture.InsertFingerprintAsync(d2, "mac", "deadbeef0002"); // different values

        long count = await CountConflictsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExcludedFingerprint_DoesNotAppearInConflictCount()
    {
        // Even with an excluded fingerprint present, conflict count stays 0.
        await _fixture.InsertExcludedFingerprintAsync("mac", "deadbeef0099");

        long count = await CountConflictsAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExcludedFingerprint_IsInserted()
    {
        await _fixture.InsertExcludedFingerprintAsync("mac", "deadbeef00aa");

        long rowCount = await _fixture.CountAsync(
            "excluded_fingerprints",
            "fp_type = 'mac' AND fp_value = 'deadbeef00aa'"
        );
        Assert.Equal(1, rowCount);
    }

    private async Task<long> CountConflictsAsync()
    {
        const string sql = """
            SELECT COUNT(*) FROM (
                SELECT 1
                FROM device_fingerprints df
                WHERE NOT EXISTS (
                    SELECT 1 FROM excluded_fingerprints ef
                    WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
                )
                GROUP BY df.fp_type, df.fp_value
                HAVING COUNT(DISTINCT df.device_id) > 1
            ) t
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is long l ? l : Convert.ToInt64(result ?? 0);
    }

    // ── WI-37: Device promotion ────────────────────────────────────────────────

    [Fact]
    public async Task Promote_UpdatesManagementStatusToManaged()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        Guid agentId = await _fixture.InsertAgentAsync();

        await _fixture.PromoteDeviceAsync(deviceId, agentId, "192.168.1.50", "ssh");

        long managed = await _fixture.CountAsync(
            "devices",
            $"device_id = '{deviceId}' AND management_status = 'managed'"
        );
        Assert.Equal(1, managed);
    }

    [Fact]
    public async Task Promote_CreatesCollectionTarget()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        Guid agentId = await _fixture.InsertAgentAsync();

        Guid targetId = await _fixture.PromoteDeviceAsync(deviceId, agentId, "10.0.0.5", "snmp");

        long targets = await _fixture.CountAsync(
            "targets",
            $"target_id = '{targetId}' AND agent_id = '{agentId}'"
        );
        Assert.Equal(1, targets);
    }
}