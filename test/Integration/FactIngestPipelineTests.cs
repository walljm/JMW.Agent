using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for FactIngestPipeline: verifies that ingesting facts
/// correctly populates projection tables (proj_systems, proj_interfaces,
/// proj_snmp_device) via a real Postgres instance.
/// </summary>
[Collection("Integration")]
public sealed class FactIngestPipelineTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public FactIngestPipelineTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private FactIngestPipeline _pipeline = null!;
    private const string DeviceId = "d1111111-1111-1111-1111-111111111111";

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
            "proj_interfaces",
            "proj_systems",
            "proj_hardware",
            "proj_devices",
            "proj_discovered",
            "facts_history"
        );
    }

    // ── Derivation input hydration (§11) ────────────────────────────────────────

    [Fact]
    public async Task Ingest_LowPriorityVendorAlone_DoesNotClobberStoredHighPriority()
    {
        // End-to-end proof of the hydration fix (docs/plans/architecture-identity-facts.md §11):
        // DeviceVendorDerivation is a priority fan-in (DeviceVendor > HwSystemVendor > …) feeding
        // proj_devices.vendor. Cycle 1 stores the high-priority DeviceVendor. Cycle 2 delivers ONLY
        // the lower-priority HwSystemVendor (as delta-tracking would when DeviceVendor is unchanged
        // and omitted). Without hydration the derivation would see only HwSystemVendor and clobber
        // the canonical; with it, the unchanged DeviceVendor is hydrated from facts_history and the
        // canonical holds.
        string deviceId = DeviceId;

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Vendor", "Ubiquiti")]);
        string? afterFirst = await ReadScalarAsync($"SELECT vendor FROM proj_devices WHERE device = '{deviceId}'");
        Assert.False(string.IsNullOrEmpty(afterFirst));

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Hardware.SystemVendor", "GenericBoardCorp")]);
        string? afterSecond = await ReadScalarAsync($"SELECT vendor FROM proj_devices WHERE device = '{deviceId}'");

        // The high-priority vendor from cycle 1 survives cycle 2's lower-priority-only batch.
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task Ingest_HwSystemVendor_PopulatesBothProjDevicesAndProjHardwareFromOneDerivation()
    {
        // DeviceVendorDerivation is the single decision-maker for "vendor" — proj_devices.vendor
        // (DeviceVendorCanonical, the fan-in's output) and proj_hardware.system_vendor both read
        // that SAME derived fact now, rather than proj_hardware reading the raw HwSystemVendor
        // input independently. One raw report, one derivation, one decision, two projections.
        string deviceId = DeviceId;

        await _pipeline.IngestAsync([Fact.Create($"Device[{deviceId}].Hardware.SystemVendor", "Acme")]);

        string? projDevicesVendor =
            await ReadScalarAsync($"SELECT vendor FROM proj_devices WHERE device = '{deviceId}'");
        string? projHardwareVendor =
            await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{deviceId}'");

        Assert.Equal("Acme", projDevicesVendor);
        Assert.Equal("Acme", projHardwareVendor);
    }

    // ── proj_systems ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_SystemFacts_PopulatesProjSystems()
    {
        string deviceId = DeviceId;
        List<Fact> facts =
        [
            Fact.Create($"Device[{deviceId}].OS.Hostname", "test-host"),
            Fact.Create($"Device[{deviceId}].OS.Family", "Linux"),
            Fact.Create($"Device[{deviceId}].OS.Distro", "Ubuntu"),
        ];

        await _pipeline.IngestAsync(facts);

        string? hostname = await ReadScalarAsync(
            $"SELECT hostname FROM proj_systems WHERE device = '{deviceId}'"
        );
        Assert.Equal("test-host", hostname);

        string? osFamily = await ReadScalarAsync(
            $"SELECT os_family FROM proj_systems WHERE device = '{deviceId}'"
        );
        // Normalized at ingest now (LowercaseTrimNormalizer on OS.Family) — the value the agent
        // used to lowercase before sending; the server does it now.
        Assert.Equal("linux", osFamily);
    }

    [Fact]
    public async Task Ingest_SystemFacts_UpdatesExistingRow()
    {
        string deviceId = DeviceId;
        List<Fact> first =
        [
            Fact.Create($"Device[{deviceId}].OS.Hostname", "host-v1"),
        ];
        List<Fact> second =
        [
            Fact.Create($"Device[{deviceId}].OS.Hostname", "host-v2"),
        ];

        await _pipeline.IngestAsync(first);
        await _pipeline.IngestAsync(second);

        string? hostname = await ReadScalarAsync(
            $"SELECT hostname FROM proj_systems WHERE device = '{deviceId}'"
        );
        Assert.Equal("host-v2", hostname);

        // Only one row per device.
        long count = await _fixture.CountAsync("proj_systems", $"device = '{deviceId}'");
        Assert.Equal(1, count);
    }

    // ── proj_interfaces ───────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_InterfaceFacts_PopulatesProjInterfaces()
    {
        string deviceId = DeviceId;
        string ifaceKey = "aabbccddeeff";
        List<Fact> facts =
        [
            Fact.Create($"Device[{deviceId}].Interface[{ifaceKey}].Name", "eth0"),
            Fact.Create($"Device[{deviceId}].Interface[{ifaceKey}].Type", "ether"),
            Fact.Create($"Device[{deviceId}].Interface[{ifaceKey}].Speed", 1000L),
        ];

        await _pipeline.IngestAsync(facts);

        string? name = await ReadScalarAsync(
            $"SELECT name FROM proj_interfaces WHERE device = '{deviceId}' AND interface = '{ifaceKey}'"
        );
        Assert.Equal("eth0", name);
    }

    [Fact]
    public async Task Ingest_MultipleInterfaces_AllRowsPresent()
    {
        string deviceId = DeviceId;
        List<Fact> facts =
        [
            Fact.Create($"Device[{deviceId}].Interface[aabbccddeeff].Name", "eth0"),
            Fact.Create($"Device[{deviceId}].Interface[112233445566].Name", "eth1"),
        ];

        await _pipeline.IngestAsync(facts);

        long count = await _fixture.CountAsync("proj_interfaces", $"device = '{deviceId}'");
        Assert.Equal(2, count);
    }

    // ── facts_history ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_Facts_AppendedToHistory()
    {
        string deviceId = DeviceId;
        List<Fact> facts =
        [
            Fact.Create($"Device[{deviceId}].OS.Hostname", "history-host"),
            Fact.Create($"Device[{deviceId}].OS.Family", "Linux"),
        ];

        await _pipeline.IngestAsync(facts);

        long count = await _fixture.CountAsync(
            "facts_history",
            $"key_values->>'Device' = '{deviceId}'"
        );
        Assert.Equal(2, count);
    }

    // ── Batch limit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_ExceedsMaxBatch_ThrowsArgumentOutOfRangeException()
    {
        string deviceId = DeviceId;
        List<Fact> oversized = Enumerable
            .Range(0, FactIngestPipeline.MaxFactsPerBatch + 1)
            .Select(i => Fact.Create($"Device[{deviceId}].OS.Hostname", $"h{i}"))
            .ToList();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _pipeline.IngestAsync(oversized)
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : result.ToString();
    }
}