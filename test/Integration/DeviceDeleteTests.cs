using JMW.Discovery.Server;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for DeviceRegistry.DeleteDeviceAsync — the manual fallback for a bad
/// auto-merge (or any device an operator wants gone), used when a true re-split isn't
/// reconstructable. Tests run against a real Postgres container.
/// </summary>
[Collection("Integration")]
public sealed class DeviceDeleteTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public DeviceDeleteTests(IntegrationFixture fixture)
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
            "device_aliases",
            "device_fingerprints",
            "devices",
            "agents",
            "proj_hardware",
            "proj_interfaces",
            "proj_services",
            "facts_history",
            "change_events",
            "incidents"
        );
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesDevicesRow()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test");

        long count = await _fixture.CountAsync("devices", $"device_id = '{deviceId}'");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesFingerprints()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(deviceId, "mac", "aabbccdd0001");

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test");

        // The core payoff: the fingerprint is free to attach to a brand-new device next time
        // this physical entity is observed, instead of resolving back into stale state.
        long count = await _fixture.CountAsync("device_fingerprints", $"device_id = '{deviceId}'");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesProjectionRows()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO proj_hardware (device, system_vendor, updated_at) VALUES (@d, 'Ghost', now())",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", deviceId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO proj_interfaces (device, interface, updated_at) VALUES (@d, 'eth0', now())",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", deviceId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test");

        long hwCount = await _fixture.CountAsync("proj_hardware", $"device = '{deviceId}'");
        long ifaceCount = await _fixture.CountAsync("proj_interfaces", $"device = '{deviceId}'");
        Assert.Equal(0, hwCount);
        Assert.Equal(0, ifaceCount);
    }

    [Fact]
    public async Task DeleteDeviceAsync_NullsProjServicesDeviceId_DoesNotDeleteServiceRow()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO proj_services (service, service_id, type, device_id, updated_at) " +
            "VALUES ('svc-1', 'svc-1', 'dns', @d, now())",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", deviceId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test");

        // The service itself is a distinct entity — deleting its host device shouldn't delete
        // the service row, just unlink it.
        long serviceStillExists = await _fixture.CountAsync("proj_services", "service = 'svc-1'");
        Assert.Equal(1, serviceStillExists);

        long stillLinked = await _fixture.CountAsync(
            "proj_services",
            $"service = 'svc-1' AND device_id = '{deviceId}'"
        );
        Assert.Equal(0, stillLinked);
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesFactsHistory()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");
        string idText = deviceId.ToString();
        string factId = $"Device[{idText}].Interface[eth0].Speed";
        string keyValues = $$"""{"Device":"{{idText}}","Interface":"eth0"}""";

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

        await _registry.DeleteDeviceAsync(idText, actor: "test");

        long count = await _fixture.CountAsync("facts_history", $"key_values ->> 'Device' = '{idText}'");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesChangeEventsAndIncidents()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");
        string idText = deviceId.ToString();

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO change_events (event_type, entity_kind, entity_id) VALUES ('discovered', 'device', @d)",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", idText);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO incidents (incident_type, entity_kind, entity_id) VALUES ('smart_failing', 'device', @d)",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", idText);
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.DeleteDeviceAsync(idText, actor: "test");

        long changeEventCount = await _fixture.CountAsync("change_events", $"entity_id = '{idText}'");
        long incidentCount = await _fixture.CountAsync("incidents", $"entity_id = '{idText}'");
        Assert.Equal(0, changeEventCount);
        Assert.Equal(0, incidentCount);
    }

    [Fact]
    public async Task DeleteDeviceAsync_NullsAgentsDeviceId()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");
        Guid agentId = await _fixture.InsertAgentAsync();

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new("UPDATE agents SET device_id = @d WHERE agent_id = @a", conn))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            cmd.Parameters.AddWithValue("a", agentId);
            await cmd.ExecuteNonQueryAsync();
        }

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test");

        long stillLinked = await _fixture.CountAsync("agents", $"agent_id = '{agentId}' AND device_id = '{deviceId}'");
        Assert.Equal(0, stillLinked);

        // The agent row itself survives — only its device link is cleared (FK ON DELETE SET NULL).
        long agentStillExists = await _fixture.CountAsync("agents", $"agent_id = '{agentId}'");
        Assert.Equal(1, agentStillExists);
    }

    [Fact]
    public async Task DeleteDeviceAsync_RemovesAliasesPointingAtIt()
    {
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        Guid loser = await _fixture.InsertDeviceAsync("managed");

        await _registry.ManualMergeAsync(loser.ToString(), survivor.ToString(), actor: "test");

        await _registry.DeleteDeviceAsync(survivor.ToString(), actor: "test");

        long aliasCount = await _fixture.CountAsync("device_aliases", $"survivor_device_id = '{survivor}'");
        Assert.Equal(0, aliasCount);
    }

    [Fact]
    public async Task DeleteDeviceAsync_WritesAuditEntry()
    {
        Guid deviceId = await _fixture.InsertDeviceAsync("managed");

        await _registry.DeleteDeviceAsync(deviceId.ToString(), actor: "test-actor");

        long auditCount = await _fixture.CountAsync(
            "audit_log",
            $"actor = 'test-actor' AND action = 'device.delete' AND target_ref = '{deviceId}'"
        );
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task DeleteDeviceAsync_NotFound_ThrowsArgumentException()
    {
        string fakeId = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _registry.DeleteDeviceAsync(fakeId, actor: "test")
        );
    }
}