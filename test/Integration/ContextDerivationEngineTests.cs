using JMW.Discovery.Server;
using JMW.Discovery.Server.Ingest.Context;
using JMW.Discovery.Server.Projections;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for ContextDerivationEngine (docs/plans/context-derivations.md): the
/// set-based resolve → change-suppressed fact emission → proj_devices identity columns path,
/// including the cross-entity friendly-name pick, the registry-only MAC pick, ranked best-IP,
/// the row-presence guarantee, and steady-state write suppression.
/// </summary>
[Collection("Integration")]
public sealed class ContextDerivationEngineTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ContextDerivationEngineTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private ContextDerivationEngine _engine = null!;

    public Task InitializeAsync()
    {
        _engine = new ContextDerivationEngine(
            _fixture.DataSource,
            new FactRepository(_fixture.DataSource, new MetricsRepository(_fixture.DataSource)),
            new ProjectionRouter(_fixture.DataSource, ProjectionLibrary.CreateAll(_fixture.DataSource)),
            ContextDerivationLibrary.CreateAll(),
            NullLogger<ContextDerivationEngine>.Instance
        );
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _engine.Dispose();
        await _fixture.TruncateAsync(
            "devices",
            "device_fingerprints",
            "device_aliases",
            "proj_systems",
            "proj_devices",
            "proj_discovered",
            "proj_interfaces",
            "proj_device_arp",
            "facts_history",
            "materialization_facts"
        );
    }

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] ps)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, object? val) in ps)
        {
            cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql, params (string Name, object? Value)[] ps)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, object? val) in ps)
        {
            cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        }

        object? result = await cmd.ExecuteScalarAsync();
        return result is string s ? s : null;
    }

    [Fact]
    public async Task RunAll_ResolvesIdentityColumns_FromCrossEntityAndRegistryState()
    {
        // Subject device: an agentless station known only through observations.
        Guid subject = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(subject, "mac", "aabbccddee10");

        // Observer device recorded a friendly name + sighting IP for that MAC in proj_discovered
        // (cross-entity: the row is keyed to the OBSERVER, matched to the subject by MAC).
        Guid observer = await _fixture.InsertDeviceAsync("managed");
        await ExecAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, friendly_name, updated_at) "
          + "VALUES (@obs, '192.168.1.40', 'aabbccddee10', 'Living Room TV', now())",
            ("obs", observer.ToString())
        );

        await _engine.RunAllAsync(CancellationToken.None);

        string dev = subject.ToString();
        Assert.Equal(
            "aabbccddee10",
            await ScalarAsync("SELECT mac FROM proj_devices WHERE device = @d", ("d", dev))
        );
        Assert.Equal(
            "Living Room TV",
            await ScalarAsync("SELECT friendly_name FROM proj_devices WHERE device = @d", ("d", dev))
        );
        Assert.Equal(
            "192.168.1.40",
            await ScalarAsync("SELECT ip FROM proj_devices WHERE device = @d", ("d", dev))
        );
    }

    [Fact]
    public async Task RunAll_HostnameAndOperatorFriendlyName_WinOverObserved()
    {
        // A managed host: proj_systems values (real hostname, operator-set friendly name)
        // outrank anything observers recorded.
        Guid id = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee11");
        await ExecAsync(
            "INSERT INTO proj_systems (device, hostname, friendly_name) VALUES (@d, 'web-01', 'The Web Box')",
            ("d", id.ToString())
        );

        await _engine.RunAllAsync(CancellationToken.None);

        Assert.Equal(
            "web-01",
            await ScalarAsync("SELECT hostname FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );
        Assert.Equal(
            "The Web Box",
            await ScalarAsync("SELECT friendly_name FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );
    }

    [Fact]
    public async Task RunAll_BestIp_PrefersFresherIdentityAddress()
    {
        // Stale last_seen_ip (.50, 2 days old) vs a fresh ARP sighting (.80, now) — recency
        // must follow the DHCP move, same regression the read-time lateral guarded.
        Guid id = await _fixture.InsertDeviceAsync("discovered");
        Guid observer = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee12");
        await ExecAsync(
            "INSERT INTO proj_systems (device, hostname, last_seen_ip, updated_at) "
          + "VALUES (@d, 'roamer', '192.168.1.50', now() - interval '2 days')",
            ("d", id.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, updated_at) "
          + "VALUES (@obs, '192.168.1.80', 'aabbccddee12', now())",
            ("obs", observer.ToString())
        );

        await _engine.RunAllAsync(CancellationToken.None);

        Assert.Equal(
            "192.168.1.80",
            await ScalarAsync("SELECT ip FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );
    }

    [Fact]
    public async Task RunAll_EnsuresRowForDeviceWithNoResolvedValues()
    {
        // A device with no MAC/hostname/anything still gets a bare proj_devices row, so it stays
        // reachable in the driving-table index walk reports use.
        Guid id = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(id, "uuid", "11112222-3333-4444-5555-666677778888");

        await _engine.RunAllAsync(CancellationToken.None);

        Assert.Equal(
            id.ToString(),
            await ScalarAsync("SELECT device FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );
    }

    [Fact]
    public async Task RunAll_SecondPass_SuppressesUnchangedWrites()
    {
        Guid id = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee13");
        await ExecAsync(
            "INSERT INTO proj_systems (device, hostname) VALUES (@d, 'static-host')",
            ("d", id.ToString())
        );

        await _engine.RunAllAsync(CancellationToken.None);
        long factsAfterFirst = await _fixture.CountAsync("facts_history");

        // Nothing changed — the second pass must emit no facts at all (engine cache suppression).
        await _engine.RunAllAsync(CancellationToken.None);
        long factsAfterSecond = await _fixture.CountAsync("facts_history");

        Assert.Equal(factsAfterFirst, factsAfterSecond);
    }

    [Fact]
    public async Task RunDue_RespectsTriggerTablesAndDebounce()
    {
        Guid id = await _fixture.InsertDeviceAsync("managed");
        await ExecAsync(
            "INSERT INTO proj_systems (device, hostname) VALUES (@d, 'gated-host')",
            ("d", id.ToString())
        );

        // A batch that touched nothing hostname-relevant must not resolve hostname.
        HashSet<string> unrelated = new(StringComparer.Ordinal) { "proj_ports" };
        await _engine.RunDueAsync(unrelated, CancellationToken.None);
        Assert.Null(await ScalarAsync("SELECT hostname FROM proj_devices WHERE device = @d", ("d", id.ToString())));

        // A batch touching proj_systems does (first pass — debounce LastRun starts at default).
        HashSet<string> relevant = new(StringComparer.Ordinal) { "proj_systems" };
        await _engine.RunDueAsync(relevant, CancellationToken.None);
        Assert.Equal(
            "gated-host",
            await ScalarAsync("SELECT hostname FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );

        // Immediately after, the debounce swallows a second relevant batch.
        await ExecAsync(
            "UPDATE proj_systems SET hostname = 'renamed-host' WHERE device = @d",
            ("d", id.ToString())
        );
        await _engine.RunDueAsync(relevant, CancellationToken.None);
        Assert.Equal(
            "gated-host",
            await ScalarAsync("SELECT hostname FROM proj_devices WHERE device = @d", ("d", id.ToString()))
        );
    }
}