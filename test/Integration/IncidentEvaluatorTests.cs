using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for IncidentEvaluator: open/resolve on value-driven incident types, the
/// filesystem_full hysteresis dead zone, and flap-suppression reopen-window behavior.
/// </summary>
[Collection("Integration")]
public sealed class IncidentEvaluatorTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private FactIngestPipeline _pipeline = null!;
    private const string DeviceId = "d2222222-2222-2222-2222-222222222222";

    public IncidentEvaluatorTests(IntegrationFixture fixture)
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

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "incidents",
            "change_events",
            "facts_history",
            "proj_disks",
            "proj_filesystems",
            "proj_containers"
        );

    [Fact]
    public async Task SmartFailing_OpensOnFailedAndResolvesOnPassed()
    {
        string deviceId = DeviceId;
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);

        (bool IsOpen, string? Detail) opened = await ReadOpenIncidentAsync(deviceId, "smart_failing");
        Assert.True(opened.IsOpen);
        Assert.Contains("FAILED", opened.Detail);

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "PASSED")]);

        (bool IsOpen, string? Detail) resolved = await ReadOpenIncidentAsync(deviceId, "smart_failing");
        Assert.False(resolved.IsOpen);
    }

    [Fact]
    public async Task FilesystemFull_HysteresisDeadZoneDoesNotResolve()
    {
        string deviceId = DeviceId;

        // Opens at >= 90%.
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Filesystem[/].UsedPercent", 92.0)]);
        Assert.True((await ReadOpenIncidentAsync(deviceId, "filesystem_full")).IsOpen);

        // Dips into the 85-90% dead zone — must stay open (no resolve, no re-open write needed).
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Filesystem[/].UsedPercent", 87.0)]);
        Assert.True((await ReadOpenIncidentAsync(deviceId, "filesystem_full")).IsOpen);

        // Only below 85% actually resolves it.
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Filesystem[/].UsedPercent", 80.0)]);
        Assert.False((await ReadOpenIncidentAsync(deviceId, "filesystem_full")).IsOpen);
    }

    [Fact]
    public async Task ContainerNotRunning_OpensOnNonRunningState()
    {
        string deviceId = DeviceId;
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Container[c1].State", "exited")]);

        (bool IsOpen, string? Detail) opened = await ReadOpenIncidentAsync(deviceId, "container_not_running");
        Assert.True(opened.IsOpen);

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Container[c1].State", "running")]);
        Assert.False((await ReadOpenIncidentAsync(deviceId, "container_not_running")).IsOpen);
    }

    [Fact]
    public async Task ReopenWindow_RecurrenceWithinWindowContinuesSameIncident()
    {
        string deviceId = DeviceId;
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);
        DateTimeOffset firstOpenedAt = await ReadOpenedAtAsync(deviceId, "smart_failing");

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "PASSED")]);
        Assert.False((await ReadOpenIncidentAsync(deviceId, "smart_failing")).IsOpen);

        // Recurs immediately — well within the 5-minute default reopen window.
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);

        (bool IsOpen, string? Detail) reopened = await ReadOpenIncidentAsync(deviceId, "smart_failing");
        Assert.True(reopened.IsOpen);
        DateTimeOffset reopenedAt = await ReadOpenedAtAsync(deviceId, "smart_failing");
        Assert.Equal(firstOpenedAt, reopenedAt); // opened_at unchanged — same incident, not a new one.

        long rowCount = await _fixture.CountAsync(
            "incidents",
            $"entity_kind = 'device' AND entity_id = '{deviceId}' AND incident_type = 'smart_failing'"
        );
        Assert.Equal(1, rowCount); // never minted a second row.
    }

    [Fact]
    public async Task ReopenOutsideWindow_StartsANewIncident()
    {
        string deviceId = DeviceId;
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "PASSED")]);

        // Backdate the resolution well outside the 5-minute reopen window.
        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlCommand cmd = new(
            "UPDATE incidents SET resolved_at = now() - interval '1 hour' "
          + "WHERE entity_kind = 'device' AND entity_id = @d AND incident_type = 'smart_failing'",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            await cmd.ExecuteNonQueryAsync();
        }

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);

        Assert.True((await ReadOpenIncidentAsync(deviceId, "smart_failing")).IsOpen);
        long rowCount = await _fixture.CountAsync(
            "incidents",
            $"entity_kind = 'device' AND entity_id = '{deviceId}' AND incident_type = 'smart_failing'"
        );
        Assert.Equal(2, rowCount); // the old resolved row, plus a genuinely new one.
    }

    [Fact]
    public async Task GetOpenIncidentCounts_GroupsByTypeAndExcludesResolved()
    {
        string deviceA = DeviceId;
        string deviceB = "d3333333-3333-3333-3333-333333333333";

        // Two open smart_failing incidents across two devices, one resolved (must not count).
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceA}].Disk[sda].Smart.OverallHealth", "FAILED")]);
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceB}].Disk[sda].Smart.OverallHealth", "FAILED")]);
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceA}].Container[c1].State", "exited")]);
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceA}].Container[c1].State", "running")]); // resolved

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        Dictionary<string, (long OpenCount, long DistinctEntities)> byType = new(StringComparer.Ordinal);
        await foreach ((string incidentType, long? openCount, long? distinctEntities) in
            conn.GetOpenIncidentCountsAsync(CancellationToken.None))
        {
            byType[incidentType] = (openCount ?? 0, distinctEntities ?? 0);
        }

        Assert.Equal((2L, 2L), byType["smart_failing"]);
        Assert.False(byType.ContainsKey("container_not_running")); // resolved — no open rows left
    }

    [Fact]
    public async Task ListRecentActivity_MergesIncidentsAndEventsMostRecentFirst()
    {
        string deviceId = DeviceId;
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "FAILED")]);
        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Disk[sda].Smart.OverallHealth", "PASSED")]);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using (NpgsqlCommand cmd = new(
            "INSERT INTO change_events (event_type, entity_kind, entity_id, occurred_at) "
          + "VALUES ('discovered', 'device', @d, now() + interval '1 second')",
            conn
        ))
        {
            cmd.Parameters.AddWithValue("d", deviceId);
            await cmd.ExecuteNonQueryAsync();
        }

        List<(string? Kind, string? TypeName, string? EntityKind, string? EntityId, string? Detail, DateTimeOffset? At,
            TimeSpan? Duration, string? Resolution, string? EntityName)> rows = await conn
            .ListRecentActivityAsync(10, CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("event", rows[0].Kind); // most recent — the change_event, backdated +1s
        Assert.Equal("discovered", rows[0].TypeName);
        Assert.Equal("resolved", rows[1].Kind);
        Assert.Equal("smart_failing", rows[1].TypeName);
        Assert.NotNull(rows[1].Duration); // resolved incidents carry their duration
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(bool IsOpen, string? Detail)> ReadOpenIncidentAsync(string deviceId, string incidentType)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT detail FROM incidents WHERE entity_kind = 'device' AND entity_id = @d "
          + "AND incident_type = @t AND resolved_at IS NULL",
            conn
        );
        cmd.Parameters.AddWithValue("d", deviceId);
        cmd.Parameters.AddWithValue("t", incidentType);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (true, reader.IsDBNull(0) ? null : reader.GetString(0)) : (false, null);
    }

    private async Task<DateTimeOffset> ReadOpenedAtAsync(string deviceId, string incidentType)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT opened_at FROM incidents WHERE entity_kind = 'device' AND entity_id = @d "
          + "AND incident_type = @t ORDER BY opened_at DESC LIMIT 1",
            conn
        );
        cmd.Parameters.AddWithValue("d", deviceId);
        cmd.Parameters.AddWithValue("t", incidentType);
        object? result = await cmd.ExecuteScalarAsync();
        return new DateTimeOffset((DateTime)result!, TimeSpan.Zero);
    }
}
