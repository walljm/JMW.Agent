using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// EXPLAIN-based index-usage guards for the report keyset queries — the COMP-009-database.md
/// "assert EXPLAIN shows index use and no offset scan" boundary that was documented but never
/// implemented (and would have failed before the context-derivation restructure). Each API's
/// BuildSql supplies the exact production SQL; parameters are substituted to the first-page
/// shape (see FirstPagePlanAsync). Tables are seeded to a size where the planner genuinely chooses
/// between a sort and an index walk (a near-empty table seq-scans everything regardless, which
/// would make this test pass vacuously green or fail spuriously).
/// </summary>
[Collection("Integration")]
public sealed class ReportPlanTests : IAsyncLifetime
{
    private const int Devices = 4_000;

    private readonly IntegrationFixture _fx;

    public ReportPlanTests(IntegrationFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        // Clear leftovers from earlier classes in the collection first: the seed below inserts
        // proj_devices/component rows for EVERY devices row, so a stray device with an existing
        // proj_devices row would fail the whole seed on its PK.
        await _fx.TruncateAsync(
            "proj_interfaces",
            "proj_hardware",
            "proj_hardware_inventory",
            "proj_devices",
            "device_fingerprints",
            "devices"
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO devices (device_id, management_status, last_seen)
            SELECT gen_random_uuid(), 'managed', now()
            FROM generate_series(1, {Devices});

            INSERT INTO proj_devices (device, hostname, friendly_name, mac, ip, vendor)
            SELECT device_id::text,
                   'host-' || row_number() OVER (),
                   'friendly-' || row_number() OVER (),
                   lpad(to_hex(row_number() OVER ()), 12, '0'),
                   '10.' || (row_number() OVER ()) % 250 || '.' || (row_number() OVER ()) / 250 % 250
                       || '.' || (row_number() OVER ()) % 250,
                   'VendorCo'
            FROM devices;

            INSERT INTO proj_hardware_inventory (device, hwcomponent, class)
            SELECT device_id::text, 'comp-' || s, 'disk'
            FROM devices, generate_series(1, 2) s;

            INSERT INTO proj_interfaces (device, interface, name, speed_bps)
            SELECT device_id::text, 'if-' || s, 'eth' || s, s * 1000000000::bigint
            FROM devices, generate_series(1, 2) s;

            INSERT INTO proj_hardware (device, cpu_model)
            SELECT device_id::text, 'cpu-' || (row_number() OVER ()) % 50
            FROM devices;

            ANALYZE devices; ANALYZE proj_devices; ANALYZE proj_hardware_inventory;
            ANALYZE proj_interfaces; ANALYZE proj_hardware;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() =>
        await _fx.TruncateAsync(
            "proj_interfaces",
            "proj_hardware",
            "proj_hardware_inventory",
            "proj_devices",
            "devices"
        );

    /// <summary>
    /// EXPLAINs the query in its first-page shape: every parameter substituted with NULL (the
    /// planner prunes the NULL-guarded filter/cursor branches exactly as the custom plan does at
    /// execution) and the LIMIT bound to a page size so top-N/index-walk planning is realistic.
    /// </summary>
    private async Task<string> FirstPagePlanAsync(string sql)
    {
        sql = System.Text.RegularExpressions.Regex.Replace(sql, @"\$\d+", "NULL");
        sql = sql.Replace("LIMIT NULL", "LIMIT 101", StringComparison.Ordinal);

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new("EXPLAIN " + sql, conn);
        List<string> lines = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    [Theory]
    [InlineData("hostname", "proj_devices_hostname_sort_idx")]
    [InlineData("friendly_name", "proj_devices_friendly_name_sort_idx")]
    [InlineData("ip", "proj_devices_ip_sort_idx")]
    [InlineData("mac", "proj_devices_mac_sort_idx")]
    [InlineData("vendor", "proj_devices_vendor_sort_idx")]
    public async Task DeviceList_IdentitySorts_WalkTheirExpressionIndex(string sort, string index)
    {
        string plan = await FirstPagePlanAsync(DeviceListApi.BuildSql(sort, dir: null));

        // The identity sort must be served by an ordered index walk over proj_devices, not a
        // full sort of the joined set. (A top-level Sort node over the whole join is exactly
        // the regression this pins against; Incremental Sort atop the index walk is fine.)
        Assert.Contains(index, plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Components_HostnameSort_DrivesFromProjDevicesIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListComponentsAsyncCommandText("hostname", dir: null));

        Assert.Contains("proj_devices_hostname_sort_idx", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Components_ClassSort_UsesDrivingTableExpressionIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListComponentsAsyncCommandText("class", dir: null));

        Assert.Contains("proj_hardware_inventory_class_sort_idx", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interfaces_HostnameSort_DrivesFromProjDevicesIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListInterfacesAsyncCommandText("hostname", dir: null));

        Assert.Contains("proj_devices_hostname_sort_idx", plan, StringComparison.Ordinal);
    }

    // Pins the 0108 zero-padded text speed index — the bigint predecessor could never serve the
    // all-text keyset cursor (the sort itself was broken until the [SortableBy] migration).
    [Fact]
    public async Task Interfaces_SpeedSort_UsesDrivingTableExpressionIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListInterfacesAsyncCommandText("speed", dir: null));

        Assert.Contains("proj_interfaces_speed_text_sort_idx", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hardware_HostnameSort_DrivesFromProjDevicesIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListHardwareAsyncCommandText("hostname", dir: null));

        Assert.Contains("proj_devices_hostname_sort_idx", plan, StringComparison.Ordinal);
    }

    // Pins the equivalence-class behavior the ListHardware.sql tiebreaker relies on: ORDER BY
    // coalesce(h.cpu_model,''), pd.device still walks proj_hardware's (expr, device) index
    // because pd.device and h.device are join-equal.
    [Fact]
    public async Task Hardware_CpuSort_UsesDrivingTableExpressionIndex()
    {
        string plan = await FirstPagePlanAsync(ReportingQueries.ListHardwareAsyncCommandText("cpu", dir: null));

        Assert.Contains("proj_hardware_cpu_model_sort_idx", plan, StringComparison.Ordinal);
    }
}