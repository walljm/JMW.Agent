using JMW.Discovery.Core;
using JMW.Discovery.Server;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Verifies FactRepository routes metric-classified facts (FactPaths.MetricPaths) to
/// metrics_raw unconditionally, and everything else to facts_history unchanged — the split
/// introduced by docs/plans/metrics-retention.md.
/// </summary>
[Collection("Integration")]
public sealed class MetricsRepositoryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private FactRepository _repo = null!;
    private const string DeviceId = "d2222222-2222-2222-2222-222222222222";

    public MetricsRepositoryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _repo = new FactRepository(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("facts_history", "metrics_raw");

    [Fact]
    public async Task AppendAsync_MetricClassifiedFact_RoutesToMetricsRaw()
    {
        string ifaceKey = "aabbccddeeff";
        List<Fact> facts = [Fact.Create($"Device[{DeviceId}].Interface[{ifaceKey}].RxBytes", 12345L)];

        await _repo.AppendAsync(facts);

        long metricsCount = await _fixture.CountAsync("metrics_raw", $"key_values->>'Device' = '{DeviceId}'");
        long historyCount = await _fixture.CountAsync("facts_history", $"key_values->>'Device' = '{DeviceId}'");
        Assert.Equal(1, metricsCount);
        Assert.Equal(0, historyCount);
    }

    [Fact]
    public async Task AppendAsync_NonMetricFact_RoutesToFactsHistory()
    {
        List<Fact> facts = [Fact.Create($"Device[{DeviceId}].OS.Hostname", "some-host")];

        await _repo.AppendAsync(facts);

        long metricsCount = await _fixture.CountAsync("metrics_raw", $"key_values->>'Device' = '{DeviceId}'");
        long historyCount = await _fixture.CountAsync("facts_history", $"key_values->>'Device' = '{DeviceId}'");
        Assert.Equal(0, metricsCount);
        Assert.Equal(1, historyCount);
    }

    [Fact]
    public async Task AppendAsync_MixedBatch_SplitsAcrossBothTables()
    {
        string ifaceKey = "aabbccddeeff";
        List<Fact> facts =
        [
            Fact.Create($"Device[{DeviceId}].Interface[{ifaceKey}].RxBytes", 1L),
            Fact.Create($"Device[{DeviceId}].Interface[{ifaceKey}].TxBytes", 2L),
            Fact.Create($"Device[{DeviceId}].OS.Hostname", "some-host"),
            Fact.Create($"Device[{DeviceId}].OS.Family", "Linux"),
        ];

        await _repo.AppendAsync(facts);

        long metricsCount = await _fixture.CountAsync("metrics_raw", $"key_values->>'Device' = '{DeviceId}'");
        long historyCount = await _fixture.CountAsync("facts_history", $"key_values->>'Device' = '{DeviceId}'");
        Assert.Equal(2, metricsCount);
        Assert.Equal(2, historyCount);
    }

    [Fact]
    public async Task AppendAsync_MetricFact_UnchangedValueAcrossPolls_InsertsBothRows_NoDedup()
    {
        // Contrast case: facts_history dedups an unchanged value; metrics_raw must not,
        // per docs/plans/metrics-retention.md §2.1 ("an unchanged value between polls is
        // itself informative for a counter").
        // Timestamps must fall on "today" — MetricPartitionService (not exercised by these
        // tests) is what provisions other days' partitions; only today's exists via the
        // migration's initial seed.
        string ifaceKey = "aabbccddeeff";
        string path = $"Device[{DeviceId}].Interface[{ifaceKey}].RxBytes";
        DateTimeOffset t1 = DateTimeOffset.UtcNow;
        DateTimeOffset t2 = t1.AddMinutes(5);

        await _repo.AppendAsync([Fact.Create(path, 500L, t1)]);
        await _repo.AppendAsync([Fact.Create(path, 500L, t2)]); // same value, later poll

        long metricsCount = await _fixture.CountAsync("metrics_raw", $"key_values->>'Device' = '{DeviceId}'");
        Assert.Equal(2, metricsCount);
    }

    [Fact]
    public async Task AppendAsync_NonMetricFact_UnchangedValueAcrossPolls_Dedups()
    {
        string path = $"Device[{DeviceId}].OS.Hostname";
        DateTimeOffset t1 = DateTimeOffset.UtcNow;
        DateTimeOffset t2 = t1.AddMinutes(5);

        await _repo.AppendAsync([Fact.Create(path, "same-host", t1)]);
        await _repo.AppendAsync([Fact.Create(path, "same-host", t2)]); // same value, later poll

        long historyCount = await _fixture.CountAsync("facts_history", $"key_values->>'Device' = '{DeviceId}'");
        Assert.Equal(1, historyCount); // deduped — no new row for an unchanged value
    }
}
