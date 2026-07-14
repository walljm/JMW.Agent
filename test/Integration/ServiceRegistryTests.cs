using JMW.Discovery.Core;
using JMW.Discovery.Server;

using Npgsql;

namespace JMW.Discovery.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// ServiceRegistry integration tests — no prior coverage existed for this class;
// written to protect the InsertFingerprintsAsync batching rewrite (per-row loop
// → single unnest() insert).
// ═════════════════════════════════════════════════════════════════════════════

[Collection("Integration")]
public sealed class ServiceRegistryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private readonly ServiceRegistry _registry;

    public ServiceRegistryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
        _registry = new ServiceRegistry(fixture.DataSource);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("service_fingerprints", "services");

    [Fact]
    public async Task IdentifyAsync_NoExistingMatch_CreatesNewServiceWithAllFingerprints()
    {
        ServiceIdentifyRequest request = new(
            "agent-1",
            new ServiceProbe(
                "dns",
                [
                    new ServiceFingerprint(ServiceFingerprintType.PrimaryZone, "home.lan"),
                    new ServiceFingerprint(ServiceFingerprintType.DhcpSubnet, "192.168.1.0/24"),
                    new ServiceFingerprint(ServiceFingerprintType.ServerName, "core-dns"),
                ]
            )
        );

        (string serviceId, bool isNew) = await _registry.IdentifyAsync(request, CancellationToken.None);

        Assert.True(isNew);
        List<(string Type, string Value)> fps = await ListFingerprintsAsync(serviceId);
        Assert.Equal(3, fps.Count);
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.PrimaryZone && f.Value == "home.lan");
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.DhcpSubnet && f.Value == "192.168.1.0/24");
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.ServerName && f.Value == "core-dns");
    }

    [Fact]
    public async Task IdentifyAsync_MatchesExistingByAnyFingerprint_ReturnsSameServiceId()
    {
        ServiceIdentifyRequest first = new(
            "agent-1",
            new ServiceProbe(
                "dns",
                [
                    new ServiceFingerprint(ServiceFingerprintType.PrimaryZone, "home.lan"),
                    new ServiceFingerprint(ServiceFingerprintType.ServerName, "core-dns-v1"),
                ]
            )
        );
        (string firstId, _) = await _registry.IdentifyAsync(first, CancellationToken.None);

        // Second probe shares only the zone fingerprint — server-name changed (a real-world
        // rename) — but that's enough to match under the "any fingerprint" matching rule.
        ServiceIdentifyRequest second = new(
            "agent-1",
            new ServiceProbe(
                "dns",
                [
                    new ServiceFingerprint(ServiceFingerprintType.PrimaryZone, "home.lan"),
                    new ServiceFingerprint(ServiceFingerprintType.ServerName, "core-dns-v2"),
                ]
            )
        );
        (string secondId, bool isNew) = await _registry.IdentifyAsync(second, CancellationToken.None);

        Assert.False(isNew);
        Assert.Equal(firstId, secondId);

        // Both the original and the newly-learned fingerprint must now be on record — this is
        // the exact multi-row upsert path the batching rewrite touches.
        List<(string Type, string Value)> fps = await ListFingerprintsAsync(firstId);
        Assert.Equal(3, fps.Count);
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.ServerName && f.Value == "core-dns-v1");
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.ServerName && f.Value == "core-dns-v2");
        Assert.Contains(fps, f => f.Type == ServiceFingerprintType.PrimaryZone && f.Value == "home.lan");
    }

    [Fact]
    public async Task IdentifyAsync_ReSubmittingIdenticalFingerprints_DoesNotDuplicateOrThrow()
    {
        ServiceIdentifyRequest request = new(
            "agent-1",
            new ServiceProbe(
                "dns",
                [
                    new ServiceFingerprint(ServiceFingerprintType.PrimaryZone, "home.lan"),
                    new ServiceFingerprint(ServiceFingerprintType.DhcpSubnet, "192.168.1.0/24"),
                ]
            )
        );

        (string firstId, _) = await _registry.IdentifyAsync(request, CancellationToken.None);
        (string secondId, bool isNew) = await _registry.IdentifyAsync(request, CancellationToken.None);

        Assert.Equal(firstId, secondId);
        Assert.False(isNew);
        List<(string Type, string Value)> fps = await ListFingerprintsAsync(firstId);
        Assert.Equal(2, fps.Count); // ON CONFLICT DO NOTHING — no duplicate rows
    }

    [Fact]
    public async Task IdentifyAsync_DifferentServiceType_DoesNotMatchAcrossTypes()
    {
        ServiceIdentifyRequest dns = new(
            "agent-1",
            new ServiceProbe("dns", [new ServiceFingerprint(ServiceFingerprintType.ServerName, "shared-name")])
        );
        ServiceIdentifyRequest other = new(
            "agent-1",
            new ServiceProbe(
                "home-assistant",
                [new ServiceFingerprint(ServiceFingerprintType.ServerName, "shared-name")]
            )
        );

        (string dnsId, _) = await _registry.IdentifyAsync(dns, CancellationToken.None);
        (string otherId, bool isNew) = await _registry.IdentifyAsync(other, CancellationToken.None);

        Assert.True(isNew);
        Assert.NotEqual(dnsId, otherId);
    }

    private async Task<List<(string Type, string Value)>> ListFingerprintsAsync(string serviceId)
    {
        List<(string Type, string Value)> results = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT fp_type, fp_value FROM service_fingerprints WHERE service_id = @id",
            conn
        );
        cmd.Parameters.AddWithValue("id", serviceId);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
