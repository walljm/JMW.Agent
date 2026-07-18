using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Functional coverage for the unified operator-facts data layer against real Postgres
/// (docs/plans/architecture-operator-facts.md §7, §5.3): the per-device operator-fact query with its
/// label join, source-scoped revert (never touches collector history), child-collection key
/// suggestions, and path-metadata upsert round-trip.
/// </summary>
[Collection("Integration")]
public sealed class OperatorFactsQueryTests
{
    private const short ManualEntry = 2;
    private const short Collector = 1;

    private readonly IntegrationFixture _fixture;

    public OperatorFactsQueryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeviceOperatorFacts_ReturnsManualEntriesOnly_WithLabel_AndRevertLeavesCollectorHistory()
    {
        await _fixture.TruncateAsync("facts_history", "fact_path_metadata");
        string dev = Guid.NewGuid().ToString();
        string vendorId = $"Device[{dev}].Vendor";
        string ifaceId = $"Device[{dev}].Interface[aa:bb:cc:dd:ee:01].Note";

        await using (NpgsqlConnection seed = await _fixture.DataSource.OpenConnectionAsync())
        {
            // Operator override of Device[].Vendor, plus a COLLECTOR row for the SAME id (must be
            // hidden from the tab and must survive the revert).
            await ExecAsync(seed,
                "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, source, source_name) VALUES "
              + "(@vid, 'Device[].Vendor', @vkv::jsonb, 1, 'Cisco', now(), 2, 'user:boss'), "
              + "(@vid, 'Device[].Vendor', @vkv::jsonb, 1, 'Netgear', now() - interval '1 hour', 1, 'snmp'), "
              + "(@iid, 'Device[].Interface[].Note', @ikv::jsonb, 1, 'uplink', now(), 2, 'user:boss')",
                ("vid", vendorId), ("vkv", $"{{\"Device\":\"{dev}\"}}"),
                ("iid", ifaceId), ("ikv", $"{{\"Device\":\"{dev}\",\"Interface\":\"aa:bb:cc:dd:ee:01\"}}"));

            // Path-level label for the vendor override (device-independent key = {}).
            await ExecAsync(seed,
                "INSERT INTO fact_path_metadata (attribute_path, key_values, label, created_by) VALUES "
              + "('Device[].Vendor', '{}'::jsonb, 'Corrected vendor', 'user:boss')");
        }

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        List<(string AttributePath, string? KeyValues, string? Value, string? Label, string SourceName, DateTimeOffset
            CollectedAt)> rows = await conn.GetDeviceOperatorFactsAsync(Guid.Parse(dev), CancellationToken.None)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        (string AttributePath, string? KeyValues, string? Value, string? Label, string SourceName, DateTimeOffset CollectedAt) vendor = rows.Single(r => r.AttributePath == "Device[].Vendor");
        Assert.Equal("Cisco", vendor.Value); // the collector 'Netgear' row is excluded (source <> 2)
        Assert.Equal("Corrected vendor", vendor.Label);

        (string AttributePath, string? KeyValues, string? Value, string? Label, string SourceName, DateTimeOffset CollectedAt) note = rows.Single(r => r.AttributePath == "Device[].Interface[].Note");
        Assert.Equal("uplink", note.Value);
        Assert.Null(note.Label);

        // Revert the vendor override — source-scoped, so only the ManualEntry row goes.
        List<FactIdResult> removed = await conn.DeleteManualFactByIdAsync(vendorId, ManualEntry, CancellationToken.None)
            .ToListAsync();
        Assert.Single(removed);

        Assert.Equal(0, await _fixture.CountAsync("facts_history", $"id = '{vendorId}' AND source = {ManualEntry}"));
        Assert.Equal(1, await _fixture.CountAsync("facts_history", $"id = '{vendorId}' AND source = {Collector}"));
    }

    [Fact]
    public async Task CollectionKeys_ReturnObservedChildKeysForDevice()
    {
        await _fixture.TruncateAsync("facts_history", "fact_path_metadata");
        string dev = Guid.NewGuid().ToString();

        await using (NpgsqlConnection seed = await _fixture.DataSource.OpenConnectionAsync())
        {
            await ExecAsync(seed,
                "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, source) VALUES "
              + "(@a, 'Device[].Interface[].Name', @akv::jsonb, 1, 'eth0', now(), 1), "
              + "(@b, 'Device[].Interface[].Name', @bkv::jsonb, 1, 'eth1', now(), 1)",
                ("a", $"Device[{dev}].Interface[aa].Name"), ("akv", $"{{\"Device\":\"{dev}\",\"Interface\":\"aa\"}}"),
                ("b", $"Device[{dev}].Interface[bb].Name"), ("bkv", $"{{\"Device\":\"{dev}\",\"Interface\":\"bb\"}}"));
        }

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<string?> keys = await conn.GetDeviceCollectionKeysAsync(dev, "Interface", CancellationToken.None)
            .Select(k => k.CollectionKey)
            .ToListAsync();

        Assert.Equal(["aa", "bb"], keys);
    }

    [Fact]
    public async Task MetadataUpsert_StripsDeviceKey_AndReplacesLabel()
    {
        await _fixture.TruncateAsync("fact_path_metadata");
        string dev = Guid.NewGuid().ToString();

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        // Insert with a full key_values (incl. Device); the upsert strips Device → stored key {}.
        await conn.UpsertFactPathMetadataAsync("Device[].Vendor", $"{{\"Device\":\"{dev}\"}}", "First", null, "user:boss",
                showInReports: null, CancellationToken.None)
            .ToListAsync();
        // A second upsert from a different device (same stripped key {}) replaces the label.
        await conn.UpsertFactPathMetadataAsync("Device[].Vendor", "{\"Device\":\"other\"}", "Second", null, "user:boss",
                showInReports: null, CancellationToken.None)
            .ToListAsync();

        Assert.Equal(1, await _fixture.CountAsync("fact_path_metadata", "attribute_path = 'Device[].Vendor'"));
        Assert.Equal(1, await _fixture.CountAsync("fact_path_metadata", "label = 'Second'"));
    }

    [Fact]
    public async Task FleetPathSearch_ListsDevicesForPath_WithLabel_AndKeysetPaginates()
    {
        await _fixture.TruncateAsync("facts_history", "fact_path_metadata");
        string dev1 = Guid.NewGuid().ToString();
        string dev2 = Guid.NewGuid().ToString();

        await using (NpgsqlConnection seed = await _fixture.DataSource.OpenConnectionAsync())
        {
            await ExecAsync(seed,
                "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, source, source_name) VALUES "
              + "(@a, 'Device[].Vendor', @akv::jsonb, 1, 'Cisco', now(), 2, 'user:boss'), "
              + "(@b, 'Device[].Vendor', @bkv::jsonb, 1, 'Ubiquiti', now(), 2, 'user:boss')",
                ("a", $"Device[{dev1}].Vendor"), ("akv", $"{{\"Device\":\"{dev1}\"}}"),
                ("b", $"Device[{dev2}].Vendor"), ("bkv", $"{{\"Device\":\"{dev2}\"}}"));
            await ExecAsync(seed,
                "INSERT INTO fact_path_metadata (attribute_path, key_values, label, created_by) VALUES "
              + "('Device[].Vendor', '{}'::jsonb, 'Corrected vendor', 'user:boss')");
        }

        (List<OperatorFactsApi.FleetFactItem> Items, string? NextCursor) page1 =
            await OperatorFactsApi.QueryFleetFactsAsync(_fixture.DataSource, "Device[].Vendor", null, null, 1,
                CancellationToken.None);

        Assert.Single(page1.Items);
        Assert.Equal("Device", page1.Items[0].Scope);
        Assert.Equal("Corrected vendor", page1.Items[0].Label);
        Assert.NotNull(page1.NextCursor);

        Assert.True(KeysetCursor.TryDecodeParts(page1.NextCursor, 2, out string[] parts));
        (List<OperatorFactsApi.FleetFactItem> Items, string? NextCursor) page2 =
            await OperatorFactsApi.QueryFleetFactsAsync(_fixture.DataSource, "Device[].Vendor", parts[0], parts[1], 1,
                CancellationToken.None);

        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);

        HashSet<string> devices = [page1.Items[0].DeviceId, page2.Items[0].DeviceId];
        Assert.Equal([dev1, dev2], devices.OrderBy(d => d, StringComparer.Ordinal).ToHashSet());
    }

    [Fact]
    public async Task FleetBrowsePaths_ReturnsDistinctPathSignatures_WithDeviceCountsAndLabels()
    {
        await _fixture.TruncateAsync("facts_history", "fact_path_metadata");
        string dev1 = Guid.NewGuid().ToString();
        string dev2 = Guid.NewGuid().ToString();

        await using (NpgsqlConnection seed = await _fixture.DataSource.OpenConnectionAsync())
        {
            await ExecAsync(seed,
                "INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, source) VALUES "
              + "(@a, 'Device[].Vendor', @akv::jsonb, 1, 'Cisco', now(), 2), "
              + "(@b, 'Device[].Vendor', @bkv::jsonb, 1, 'Ubiquiti', now(), 2), "
              + "(@c, 'Device[].Interface[].Note', @ckv::jsonb, 1, 'uplink', now(), 2)",
                ("a", $"Device[{dev1}].Vendor"), ("akv", $"{{\"Device\":\"{dev1}\"}}"),
                ("b", $"Device[{dev2}].Vendor"), ("bkv", $"{{\"Device\":\"{dev2}\"}}"),
                ("c", $"Device[{dev1}].Interface[aa].Note"), ("ckv", $"{{\"Device\":\"{dev1}\",\"Interface\":\"aa\"}}"));
        }

        (List<OperatorFactsApi.FleetPathItem> Items, string? _) result =
            await OperatorFactsApi.QueryFleetPathsAsync(_fixture.DataSource, null, null, 50, CancellationToken.None);

        OperatorFactsApi.FleetPathItem vendor = result.Items.Single(i => i.AttributePath == "Device[].Vendor");
        Assert.Equal(2, vendor.DeviceCount); // shared across both devices, one signature
        Assert.Equal("Device", vendor.Scope);

        OperatorFactsApi.FleetPathItem note = result.Items.Single(i => i.AttributePath == "Device[].Interface[].Note");
        Assert.Equal(1, note.DeviceCount);
        Assert.Equal("Interface[aa]", note.Scope);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, string Value)[] ps)
    {
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, string value) in ps)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}