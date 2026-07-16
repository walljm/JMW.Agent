using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// DeviceQueries integration tests — regression coverage for the obscured-MAC
// identity-contamination bug class (see ReportingApiTests.DeviceListApiTests and
// DiscoveryMaterializerTests for the same guard applied to sibling queries).
// ═════════════════════════════════════════════════════════════════════════════

[Collection("Integration")]
public sealed class DeviceQueriesTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public DeviceQueriesTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "facts_history",
            "metrics_raw",
            "proj_discovered",
            "proj_systems",
            "device_fingerprints",
            "devices"
        );

    [Fact]
    public async Task GetDeviceAllFactsAsync_ObscuredMacReconstructedRow_NeverContaminatesSightingFacts()
    {
        // Same contamination bug class already fixed in GetPromotionGapRows.sql / DeviceListApi.cs /
        // GetDeviceSummary.sql: a Google Wifi/OnHub row's `mac` can be a RECONSTRUCTED value
        // (obscured_mac IS NOT NULL) that happens to equal this device's real MAC fingerprint
        // without actually being the same physical device. The "sighting" CTE must exclude it.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "187f881bcdb1");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, obscured_mac, sources, updated_at) "
          + "VALUES ('google-wifi-ap', '192.168.1.214', '187f881bcdb1', '187f889c4e6*', 'google-wifi', now())"
        );
        await ExecuteAsync(
            "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at) "
          + "VALUES ('Device[google-wifi-ap].Discovered[192.168.1.214].HttpTitle', "
          + "'Device[].Discovered[].HttpTitle', "
          + "'{\"Device\": \"google-wifi-ap\", \"Discovered\": \"192.168.1.214\"}'::jsonb, "
          + "1, 'Someone Else''s Login Page', now())"
        );

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await foreach ((string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName, DateTimeOffset? CollectedAt) row in conn.GetDeviceAllFactsAsync(id, CancellationToken.None))
        {
            facts.Add(row);
        }

        // This attribute_path only exists on the fact inserted above, and the "sighting" CTE is
        // the only branch of the UNION that could ever surface a Discovered[]-scoped fact for a
        // device keyed by device_id — if it appears, the obscured-mac guard isn't working.
        Assert.DoesNotContain(facts, f => f.AttributePath == "Device[].Discovered[].HttpTitle");
    }

    [Fact]
    public async Task GetDeviceAllFactsAsync_SameFactSeenByTwoObservers_MergesIntoOneRowWithBothOrigins()
    {
        // The bug this query used to have: two different observers ("observer-a"/"observer-b")
        // each produce their own facts_history row for the same real-world sighting (same
        // attribute_path + value), differing only in key_values.Device (the observer) — no
        // natural key collapsed them, so the All Facts tab showed the same fact twice. The fix
        // groups by attribute_path + value + (key_values minus Device/Discovered) and merges
        // origin/source_name into one comma-joined string per group.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee01");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources, updated_at) VALUES "
          + "('observer-a', '192.168.1.30', 'aabbccddee01', 'arp', now()), "
          + "('observer-b', '192.168.1.31', 'aabbccddee01', 'mdns', now())"
        );
        await ExecuteAsync(
            "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, "
          + "source_name) VALUES "
          + "('Device[observer-a].Discovered[192.168.1.30].HttpTitle', 'Device[].Discovered[].HttpTitle', "
          + "'{\"Device\": \"observer-a\", \"Discovered\": \"192.168.1.30\"}'::jsonb, 1, 'Nest Wifi', now(), "
          + "'HttpBanner'), "
          + "('Device[observer-b].Discovered[192.168.1.31].HttpTitle', 'Device[].Discovered[].HttpTitle', "
          + "'{\"Device\": \"observer-b\", \"Discovered\": \"192.168.1.31\"}'::jsonb, 1, 'Nest Wifi', now(), "
          + "'Mdns')"
        );

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = await GetAllFactsAsync(id);

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> matches =
                facts.Where(f => f.AttributePath == "Device[].Discovered[].HttpTitle").ToList();
        (string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt) only = Assert.Single(matches);
        Assert.Equal("Nest Wifi", only.Value);
        Assert.Equal("observer-a, observer-b", only.Origin);
        Assert.Equal("HttpBanner, Mdns", only.SourceName);
    }

    [Fact]
    public async Task GetDeviceAllFactsAsync_SameAttributeDifferentValues_StaysAsSeparateRows()
    {
        // Guards against the fix being overbroad: if two observers genuinely disagree on the
        // value, that's real information (e.g. one has a stale reading) — it must NOT be
        // silently collapsed into one row.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee02");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources, updated_at) VALUES "
          + "('observer-c', '192.168.1.40', 'aabbccddee02', 'arp', now()), "
          + "('observer-d', '192.168.1.41', 'aabbccddee02', 'mdns', now())"
        );
        await ExecuteAsync(
            "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at) "
          + "VALUES "
          + "('Device[observer-c].Discovered[192.168.1.40].HttpTitle', 'Device[].Discovered[].HttpTitle', "
          + "'{\"Device\": \"observer-c\", \"Discovered\": \"192.168.1.40\"}'::jsonb, 1, 'Google Wifi', now()), "
          + "('Device[observer-d].Discovered[192.168.1.41].HttpTitle', 'Device[].Discovered[].HttpTitle', "
          + "'{\"Device\": \"observer-d\", \"Discovered\": \"192.168.1.41\"}'::jsonb, 1, 'Nest Wifi', now())"
        );

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = await GetAllFactsAsync(id);

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> matches =
                facts.Where(f => f.AttributePath == "Device[].Discovered[].HttpTitle").ToList();
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, f => f.Value == "Google Wifi" && f.Origin == "observer-c");
        Assert.Contains(matches, f => f.Value == "Nest Wifi" && f.Origin == "observer-d");
    }

    [Fact]
    public async Task GetDeviceAllFactsAsync_MetricClassifiedFact_SurfacedFromMetricsRaw()
    {
        // docs/plans/metrics-retention.md §2.6: metric-classified paths (interface Rx/Tx
        // bytes/packets, etc.) never land in facts_history — the "own_metrics" CTE must
        // surface their current value from metrics_raw instead, or the device-detail
        // "Interface Counters" table goes blank.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await ExecuteAsync(
            "INSERT INTO metrics_raw (id, attribute_path, key_values, kind, value_long, collected_at, "
          + "source, source_name) VALUES "
          + $"('Device[{id}].Interface[aabbccddeeff].RxBytes', 'Device[].Interface[].RxBytes', "
          + $"'{{\"Device\": \"{id}\", \"Interface\": \"aabbccddeeff\"}}'::jsonb, 1, 999888, now(), 0, 'Agent')"
        );

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = await GetAllFactsAsync(id);

        (string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt) row =
                Assert.Single(facts, f => f.AttributePath == "Device[].Interface[].RxBytes");
        Assert.Equal("999888", row.Value);
        Assert.Equal("own", row.Origin);
    }

    [Fact]
    public async Task GetDeviceAllFactsAsync_MetricPath_ExcludesStaleFactsHistoryDuplicate()
    {
        // A pre-cutover facts_history row for a now-metric-classified path is frozen forever
        // (no new writes land there for that path). Without the exclusion filter in "own", it
        // would UNION ALL alongside the live metrics_raw value with no deterministic tiebreak.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await ExecuteAsync(
            "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_long, collected_at) "
          + "VALUES "
          + $"('Device[{id}].Interface[aabbccddeeff].RxBytes', 'Device[].Interface[].RxBytes', "
          + $"'{{\"Device\": \"{id}\", \"Interface\": \"aabbccddeeff\"}}'::jsonb, 1, 111, now() - interval '10 days')"
        );
        await ExecuteAsync(
            "INSERT INTO metrics_raw (id, attribute_path, key_values, kind, value_long, collected_at, "
          + "source, source_name) VALUES "
          + $"('Device[{id}].Interface[aabbccddeeff].RxBytes', 'Device[].Interface[].RxBytes', "
          + $"'{{\"Device\": \"{id}\", \"Interface\": \"aabbccddeeff\"}}'::jsonb, 1, 999888, now(), 0, 'Agent')"
        );

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = await GetAllFactsAsync(id);

        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> matches =
                facts.Where(f => f.AttributePath == "Device[].Interface[].RxBytes").ToList();
        (string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt) only = Assert.Single(matches);
        Assert.Equal("999888", only.Value); // the metrics_raw value, not the stale 111
    }

    [Fact]
    public async Task GetDeviceSightingsAsync_ObscuredMacReconstructedRow_NeverContaminatesSeenByTab()
    {
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "187f881bcdb1");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, obscured_mac, sources, updated_at) "
          + "VALUES ('google-wifi-ap', '192.168.1.214', '187f881bcdb1', '187f889c4e6*', 'google-wifi', now())"
        );

        List<(string ObserverId, string? ObserverHostname, string Ip, string? Sources, string? Oui,
            string? OuiCountry, string? Services)> sightings = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await foreach ((string ObserverId, string? ObserverHostname, string Ip, string? Sources, string? Oui,
            string? OuiCountry, string? Services) row in conn.GetDeviceSightingsAsync(id, CancellationToken.None))
        {
            sightings.Add(row);
        }

        Assert.Empty(sightings);
    }

    [Fact]
    public async Task GetDeviceSightingsAsync_RealMacNoObscuration_StillReturnsSighting()
    {
        // Guards against the fix being overbroad: a genuine (non-obscured) sighting must still
        // show up — this isn't testing "never show sightings", just "never trust reconstructed MACs".
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "187f881bcdb1");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, obscured_mac, sources, updated_at) "
          + "VALUES ('some-observer', '192.168.1.50', '187f881bcdb1', NULL, 'arp', now())"
        );

        List<(string ObserverId, string? ObserverHostname, string Ip, string? Sources, string? Oui,
            string? OuiCountry, string? Services)> sightings = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await foreach ((string ObserverId, string? ObserverHostname, string Ip, string? Sources, string? Oui,
            string? OuiCountry, string? Services) row in conn.GetDeviceSightingsAsync(id, CancellationToken.None))
        {
            sightings.Add(row);
        }

        Assert.Single(sightings);
        Assert.Equal("some-observer", sightings[0].ObserverId);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<(string? AttributePath, string? KeyValues, string? Value, string? Origin,
        string? SourceName, DateTimeOffset? CollectedAt)>> GetAllFactsAsync(Guid id)
    {
        List<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> facts = [];
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await foreach ((string? AttributePath, string? KeyValues, string? Value, string? Origin,
            string? SourceName, DateTimeOffset? CollectedAt) row in conn.GetDeviceAllFactsAsync(
                id,
                CancellationToken.None
            ))
        {
            facts.Add(row);
        }

        return facts;
    }
}