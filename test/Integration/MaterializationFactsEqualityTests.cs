using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Equality-fitness net for Phase 1-2 of docs/plans/architecture-identity-facts.md §7: after
/// ingesting facts covering every moving identity signal, the materialization_facts pivot must
/// equal proj_discovered's wide columns row-for-row. This is the safety net that proves reads can
/// move onto the narrow table one query family at a time (§7 Phase 2) without observing a
/// different world — deleted once the wide columns are retired (§7 Phase 3).
/// </summary>
[Collection("Integration")]
public sealed class MaterializationFactsEqualityTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public MaterializationFactsEqualityTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private FactIngestPipeline _pipeline = null!;
    private const string DeviceId = "d2222222-2222-2222-2222-222222222222";
    private const string Ip = "10.0.0.55";

    public Task InitializeAsync()
    {
        List<IProjection> projections =
        [
            .. ProjectionLibrary.CreateAll(_fixture.DataSource),
            new IdentityFactProjection(NullLogger<IdentityFactProjection>.Instance),
        ];
        FactRepository repo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        ProjectionRouter router = new(_fixture.DataSource, projections);
        IncidentEvaluator incidents = new(_fixture.DataSource, IncidentTypeRegistry.CreateAll());
        _pipeline = new FactIngestPipeline(repo, router, AnalysisLibrary.CreateEngine(), incidents);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync("proj_discovered", "materialization_facts", "facts_history");
    }

    [Fact]
    public async Task NarrowPivot_MatchesWideColumns_ForEveryMovingSignal()
    {
        string prefix = $"Device[{DeviceId}].Discovered[{Ip}]";
        List<Fact> facts =
        [
            Fact.Create($"{prefix}.OnvifSerial", "onvif-serial-1"),
            Fact.Create($"{prefix}.RokuSerial", "roku-serial-1"),
            Fact.Create($"{prefix}.SnmpSerial", "snmp-serial-1"),
            Fact.Create($"{prefix}.SsdpUuid", "4e2b3a10-1111-4a2b-8c3d-000000000001"),
            Fact.Create($"{prefix}.WsdUuid", "4e2b3a10-2222-4a2b-8c3d-000000000002"),
            Fact.Create($"{prefix}.HueBridgeId", "hue-bridge-1"),
            Fact.Create($"{prefix}.OnvifHardwareId", "onvif-hw-1"),
            Fact.Create($"{prefix}.CastId", "cast-1"),
            Fact.Create($"{prefix}.DeviceType", "Nest-Audio"),
            Fact.Create($"{prefix}.Os", "linux"),
            Fact.Create($"{prefix}.SshHostKey", "ssh-host-key-1"),
        ];

        await _pipeline.IngestAsync(facts);

        // Read the wide row and assert every column actually landed (non-null) — some paths are
        // normalized before either projection sees them (e.g. a bare serial gets a "bare:" vendor
        // scope prefix), so the equality assertions below (not a hardcoded literal) are the real
        // test: both projections see the SAME post-normalization value, since both are fed by the
        // same routed fact.
        (string? OnvifSerial, string? RokuSerial, string? SnmpSerial, string? SsdpUuid, string? WsdUuid, string?
            HueBridgeId, string? OnvifHardwareId, string? CastId, string? DeviceType, string? Os, string?
            SshHostKey) wide = await ReadWideRowAsync();

        Assert.NotNull(wide.OnvifSerial);
        Assert.NotNull(wide.RokuSerial);
        Assert.NotNull(wide.SnmpSerial);
        Assert.NotNull(wide.SsdpUuid);
        Assert.NotNull(wide.WsdUuid);
        Assert.NotNull(wide.HueBridgeId);
        Assert.NotNull(wide.OnvifHardwareId);
        Assert.NotNull(wide.CastId);
        Assert.NotNull(wide.DeviceType);
        Assert.NotNull(wide.Os);
        Assert.NotNull(wide.SshHostKey);

        Dictionary<string, string> narrow = await ReadNarrowPivotAsync();

        Assert.Equal(wide.OnvifSerial, narrow.GetValueOrDefault("Device[].Discovered[].OnvifSerial"));
        Assert.Equal(wide.RokuSerial, narrow.GetValueOrDefault("Device[].Discovered[].RokuSerial"));
        Assert.Equal(wide.SnmpSerial, narrow.GetValueOrDefault("Device[].Discovered[].SnmpSerial"));
        Assert.Equal(wide.SsdpUuid, narrow.GetValueOrDefault("Device[].Discovered[].SsdpUuid"));
        Assert.Equal(wide.WsdUuid, narrow.GetValueOrDefault("Device[].Discovered[].WsdUuid"));
        Assert.Equal(wide.HueBridgeId, narrow.GetValueOrDefault("Device[].Discovered[].HueBridgeId"));
        Assert.Equal(wide.OnvifHardwareId, narrow.GetValueOrDefault("Device[].Discovered[].OnvifHardwareId"));
        Assert.Equal(wide.CastId, narrow.GetValueOrDefault("Device[].Discovered[].CastId"));
        Assert.Equal(wide.DeviceType, narrow.GetValueOrDefault("Device[].Discovered[].DeviceType"));
        Assert.Equal(wide.Os, narrow.GetValueOrDefault("Device[].Discovered[].Os"));
        Assert.Equal(wide.SshHostKey, narrow.GetValueOrDefault("Device[].Discovered[].SshHostKey"));

        // Every narrow row is keyed under the discovered IP as entity_key, and the device column.
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand countCmd = new(
            $"SELECT COUNT(*) FROM materialization_facts WHERE device = '{DeviceId}' AND entity_key = '{Ip}'",
            conn
        );
        long count = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
        Assert.Equal(11, count);
    }

    private async Task<(string?, string?, string?, string?, string?, string?, string?, string?, string?, string?,
        string?)> ReadWideRowAsync()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            """
            SELECT onvif_serial, roku_serial, snmp_serial, ssdp_uuid, wsd_uuid, hue_bridge_id,
                   onvif_hardware_id, cast_id, device_type, os, ssh_host_key
            FROM proj_discovered WHERE device = @device AND discovered = @ip
            """,
            conn
        );
        cmd.Parameters.AddWithValue("device", DeviceId);
        cmd.Parameters.AddWithValue("ip", Ip);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        bool has = await reader.ReadAsync();
        Assert.True(has, "proj_discovered has no row for the ingested device/ip.");

        string? Col(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return (Col(0), Col(1), Col(2), Col(3), Col(4), Col(5), Col(6), Col(7), Col(8), Col(9), Col(10));
    }

    private async Task<Dictionary<string, string>> ReadNarrowPivotAsync()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            """
            SELECT attribute_path, value FROM materialization_facts
            WHERE device = @device AND entity_key = @ip
            """,
            conn
        );
        cmd.Parameters.AddWithValue("device", DeviceId);
        cmd.Parameters.AddWithValue("ip", Ip);

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}
