using System.Net;
using System.Text.Json;

using JMW.Discovery.Server.Admin;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Tests;

// ═════════════════════════════════════════════════════════════════════════════
// Reporting API integration tests
//
// Each test class covers one endpoint's QueryAsync method against a real
// Postgres instance (via IntegrationFixture / Testcontainers).
//
// Pattern per class:
//   InitializeAsync  — no-op or light setup
//   DisposeAsync     — truncates only the tables touched by this class
//   Seed helpers     — private raw-SQL inserts for this class's tables
//
// ═════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────────────
// DeviceListApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class DeviceListApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public DeviceListApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "proj_systems",
            "proj_devices",
            "proj_hardware",
            "proj_device_arp",
            "proj_dhcp_leases",
            "proj_discovered",
            "proj_interfaces",
            "device_fingerprints",
            "devices",
            "facts_history",
            "materialization_facts",
            "oui_entries"
        );

    /// <summary>
    /// Runs one full context-derivation pass, resolving the identity columns
    /// (hostname/friendly_name/mac/ip) onto proj_devices from the seeded observation state —
    /// the production step that happens on ingest. The device list reads those columns; the
    /// resolution rules themselves are pinned by ContextDerivationEngineTests and the tests
    /// below become end-to-end: seed observations → resolve → report shows the resolved value.
    /// </summary>
    private async Task ResolveIdentityAsync()
    {
        using JMW.Discovery.Server.Ingest.Context.ContextDerivationEngine engine = new(
            _fixture.DataSource,
            new JMW.Discovery.Server.FactRepository(
                _fixture.DataSource,
                new JMW.Discovery.Server.MetricsRepository(_fixture.DataSource)
            ),
            new JMW.Discovery.Server.Projections.ProjectionRouter(
                _fixture.DataSource,
                JMW.Discovery.Server.Projections.ProjectionLibrary.CreateAll(_fixture.DataSource)
            ),
            JMW.Discovery.Server.Ingest.Context.ContextDerivationLibrary.CreateAll(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                JMW.Discovery.Server.Ingest.Context.ContextDerivationEngine>.Instance
        );
        await engine.RunAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Query_NoDevices_ReturnsEmptyList()
    {
        (List<DeviceReportItem> items, string? next) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_SingleDevice_ReturnsCorrectFields()
    {
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await InsertSystemAsync(id, hostname: "web-01", osFamily: "Linux", osDistro: "Ubuntu");
        // Vendor comes from proj_devices.vendor — DeviceVendorDerivation's canonical output.
        await InsertDeviceVendorAsync(id, vendor: "Dell");

        (List<DeviceReportItem> items, string? next) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(next);

        DeviceReportItem h = items[0];
        Assert.Equal(id.ToString(), h.DeviceId);
        Assert.Equal("web-01", h.Hostname);
        Assert.Equal("Linux", h.OsFamily);
        Assert.Equal("Ubuntu", h.OsDistro);
        Assert.Equal("Dell", h.Vendor);
        Assert.Equal("managed", h.ManagementStatus);
    }

    [Fact]
    public async Task Query_ObscuredMacOnly_FallsBackToObscuredMacAndOui()
    {
        // Google Wifi/OnHub reports MACs with the device bytes masked (ObscuredMac.cs). When no
        // ARP/DHCP sighting lets the server reconstruct the real MAC, the host should still show
        // the obscured value and its OUI-derived vendor rather than "—" for everything.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await InsertSystemAsync(id, hostname: "chromecast-01");
        await _fixture.InsertFingerprintAsync(id, "obscured-mac", "00e0bf1fc40*");
        await SeedOuiAsync("00e0bf", "Google Inc.");

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        DeviceReportItem h = items[0];
        Assert.Null(h.Mac);
        Assert.Equal("00e0bf1fc40*", h.ObscuredMac);
        Assert.Equal("Google Inc.", h.Oui);
    }

    [Fact]
    public async Task Query_RealMacAndObscuredMacBothPresent_PrefersRealMacOui()
    {
        // Once ObscuredMac reconstruction succeeds, the real MAC fingerprint is minted alongside
        // the retained obscured one (DiscoveryMaterializer.cs). The real MAC's OUI must win even
        // when it differs from the obscured OUI — regression guard for the COALESCE precedence.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await InsertSystemAsync(id, hostname: "chromecast-02");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbcc112233");
        await _fixture.InsertFingerprintAsync(id, "obscured-mac", "00e0bf1fc40*");
        await SeedOuiAsync("aabbcc", "Real Vendor Inc.");
        await SeedOuiAsync("00e0bf", "Google Inc.");

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        DeviceReportItem h = items[0];
        Assert.Equal("aabbcc112233", h.Mac);
        Assert.Equal("00e0bf1fc40*", h.ObscuredMac);
        Assert.Equal("Real Vendor Inc.", h.Oui);
    }

    [Fact]
    public async Task Query_NoProjSystemsRow_FallsBackToDiscoveredNameForFriendlyName()
    {
        // A passively-discovered device (e.g. a smart speaker found via mDNS/Cast scan) has no
        // agent, so it never gets a proj_systems row — Hostname stays null (there's no real OS
        // hostname to report). Its name is only known through another observer's proj_discovered
        // sighting — that must still surface as the shown FriendlyName rather than "—".
        // Regression guard for the agentless-device display-name gap.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "d88c79420abf");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, hostname, sources, updated_at) "
          + "VALUES ('observer-1', '192.168.1.211', 'd88c79420abf', 'Kitchen Audio', 'eureka', now())"
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(items[0].Hostname);
        Assert.Equal("Kitchen Audio", items[0].FriendlyName);
    }

    [Fact]
    public async Task Query_DiscoveredFriendlyNamePrefersFriendlyNameOverHostname()
    {
        // When an observer recorded both a raw mDNS hostname and a human-set friendly name for
        // the same device, the friendly name should win as the more meaningful display value.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "d88c79420abc");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, hostname, friendly_name, sources, updated_at) "
          + "VALUES ('observer-1', '192.168.1.212', 'd88c79420abc', 'raw-mdns-name.local', 'Living Room Speaker', 'eureka', now())"
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(items[0].Hostname);
        Assert.Equal("Living Room Speaker", items[0].FriendlyName);
    }

    [Fact]
    public async Task Query_ObscuredMacReconstructedRow_NameNeverSmearsOntoReconstructedMacDevice()
    {
        // A Google Wifi/OnHub row's `mac` can get filled in by obscured-MAC reconstruction
        // (SetDiscoveredMac.sql) rather than direct observation: a stale mDNS advertisement can
        // reconstruct to a totally different device's real MAC once another observer
        // corroborates it by IP/OUI. That row's friendly_name describes the ORIGINAL
        // (obscured-mac/cast-id) entity, not whatever device the reconstructed MAC belongs to —
        // it must never leak onto this device via the hostname fallback. Regression guard for
        // the same contamination bug found in the promotion-gap materializer pass.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await InsertSystemAsync(id, hostname: null);
        await _fixture.InsertFingerprintAsync(id, "mac", "187f881bcdb1");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, obscured_mac, friendly_name, sources, updated_at) "
          + "VALUES ('google-wifi-ap', '192.168.1.214', '187f881bcdb1', '187f889c4e6*', "
          + "'Mother In Law Suite speaker', 'google-wifi', now())"
        );

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(items[0].Hostname);
    }

    private async Task SeedOuiAsync(string prefix, string vendor)
    {
        const string sql = """
            INSERT INTO oui_entries (prefix, bits, vendor)
            VALUES (@prefix, 24, @vendor)
            ON CONFLICT (prefix, bits) DO UPDATE SET vendor = EXCLUDED.vendor
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("prefix", prefix);
        cmd.Parameters.AddWithValue("vendor", vendor);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Query_FilterByStatus_ReturnsOnlyMatchingDevices()
    {
        Guid managed = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        Guid discovered = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await InsertSystemAsync(managed, hostname: "managed-host");
        await InsertSystemAsync(discovered, hostname: "discovered-host");

        (List<DeviceReportItem> managed_items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            "managed",
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        (List<DeviceReportItem> discovered_items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            "discovered",
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.All(managed_items, i => Assert.Equal("managed", i.ManagementStatus));
        Assert.All(discovered_items, i => Assert.Equal("discovered", i.ManagementStatus));
        Assert.DoesNotContain(managed_items, i => i.ManagementStatus == "discovered");
    }

    [Fact]
    public async Task Query_FilterByOs_ReturnsOnlyMatchingDevices()
    {
        Guid linux = await _fixture.InsertDeviceAsync("managed");
        Guid windows = await _fixture.InsertDeviceAsync("managed");
        await InsertSystemAsync(linux, hostname: "linux-box", osFamily: "Linux");
        await InsertSystemAsync(windows, hostname: "win-box", osFamily: "Windows");

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            "Linux",
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.All(items, i => Assert.Equal("Linux", i.OsFamily));
        Assert.DoesNotContain(items, i => i.OsFamily == "Windows");
    }

    [Fact]
    public async Task Query_FilterByVendor_ReturnsOnlyMatchingDevices()
    {
        Guid dell = await _fixture.InsertDeviceAsync("managed");
        Guid hp = await _fixture.InsertDeviceAsync("managed");
        await InsertSystemAsync(dell, hostname: "dell-box");
        await InsertSystemAsync(hp, hostname: "hp-box");
        await InsertDeviceVendorAsync(dell, vendor: "Dell");
        await InsertDeviceVendorAsync(hp, vendor: "HP");

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            "Dell",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        // Assert.Single first: an over-aggressive filter returning zero rows would make the
        // two negative assertions below pass vacuously.
        Assert.Single(items);
        Assert.All(items, i => Assert.Equal("Dell", i.Vendor));
        Assert.DoesNotContain(items, i => i.Vendor == "HP");
    }

    [Fact]
    public async Task Query_SearchByHostname_FiltersResults()
    {
        Guid match = await _fixture.InsertDeviceAsync("managed");
        Guid noMatch = await _fixture.InsertDeviceAsync("managed");
        await InsertSystemAsync(match, hostname: "database-server-01");
        await InsertSystemAsync(noMatch, hostname: "webserver-01");

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            "database",
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("database-server-01", items[0].Hostname);
    }

    [Fact]
    public async Task Query_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        // Insert 3 devices with hostnames "alpha", "beta", "gamma".
        Guid a = await _fixture.InsertDeviceAsync("managed");
        Guid b = await _fixture.InsertDeviceAsync("managed");
        Guid c = await _fixture.InsertDeviceAsync("managed");
        await InsertSystemAsync(a, hostname: "alpha");
        await InsertSystemAsync(b, hostname: "beta");
        await InsertSystemAsync(c, hostname: "gamma");

        // Page 1: limit=2 should return alpha+beta and a cursor. Sort explicitly by hostname —
        // this test is about pagination mechanics, not about whatever DeviceListApi.DefaultSort
        // happens to be.
        await ResolveIdentityAsync();

        (List<DeviceReportItem> page1, string? cursor1) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "hostname"
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);

        // Decode cursor and use it for page 2.
        Assert.True(KeysetCursor.TryDecode(cursor1, out string afterHostname, out string afterDeviceId));

        (List<DeviceReportItem> page2, string? cursor2) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            afterHostname,
            afterDeviceId,
            2,
            CancellationToken.None,
            sort: "hostname"
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("gamma", page2[0].Hostname);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertSystemAsync(
        Guid deviceId,
        string? hostname = null,
        string? osFamily = null,
        string? osDistro = null
    )
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname, os_family, os_distro)
            VALUES (@device, @hostname, @osFamily, @osDistro)
            ON CONFLICT (device) DO UPDATE
              SET hostname = EXCLUDED.hostname,
                  os_family = EXCLUDED.os_family,
                  os_distro = EXCLUDED.os_distro
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", deviceId.ToString());
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("osFamily", (object?)osFamily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("osDistro", (object?)osDistro ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds proj_devices.vendor — the unified cross-protocol vendor that
    /// DeviceVendorDerivation writes (hydrated fan-in of DMI/BACnet/Modbus/guess inputs).
    /// The devices report reads this column alone; a raw proj_hardware.system_vendor with no
    /// derivation run is not a state production produces (the same batch that routes
    /// HwSystemVendor also derives the canonical vendor).</summary>
    private async Task InsertDeviceVendorAsync(Guid deviceId, string? vendor = null)
    {
        const string sql = """
            INSERT INTO proj_devices (device, vendor)
            VALUES (@device, @vendor)
            ON CONFLICT (device) DO UPDATE SET vendor = EXCLUDED.vendor
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", deviceId.ToString());
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Query_BestIp_PrefersMostRecentlySeenOverStaleLastSeenIp()
    {
        // A discovered device moved by DHCP: its frozen last_seen_ip (.50) is stale, but
        // a fresh ARP sighting has it at .80. The shown IP must follow the move → .80,
        // not the older last_seen_ip. Regression guard for the recency-aware best-IP.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "001122334499");
        await ExecuteAsync(
            $"INSERT INTO proj_systems (device, hostname, last_seen_ip, updated_at) "
          + $"VALUES ('{id}', 'roamer', '192.168.1.50', now() - interval '2 days')"
        );
        await ExecuteAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES ('observer-1', '192.168.1.80', '001122334499', 'eth0', 'reachable', now())"
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("192.168.1.80", items[0].Ip);
    }

    [Fact]
    public async Task Query_BestIp_NeverLoopback_PrefersLanOverPublicWan()
    {
        // Reported bug: a multi-homed router (Google Wifi) surfaced 127.0.0.1 in the host
        // list. Its lo interface is the most-recently-updated (so it won the old arbitrary
        // recency tiebreak) and its loopback flag is NULL (so the old flag-based guard,
        // "loopback IS DISTINCT FROM TRUE", let it through). A public WAN interface is also
        // present. The identifying IP must skip the loopback AND the public WAN, landing on
        // the private LAN address. loopback column left NULL on purpose — the fix ranks by
        // address value, not the unreliable flag.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "703acbeaa759");
        await ExecuteAsync(
            "INSERT INTO proj_interfaces (device, interface, ipv4, updated_at) VALUES "
          + $"('{id}', 'lo', '127.0.0.1', now()), " // most recent → old code picked it
          + $"('{id}', 'wan0', '70.106.253.205', now() - interval '1 min'), " // public WAN
          + $"('{id}', 'br-lan', '192.168.1.1', now() - interval '2 min')" // private LAN → the answer
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("192.168.1.1", items[0].Ip);
    }

    [Fact]
    public async Task Query_BestIp_FallsBackToDiscoveredConnectIp_WhenNoInterfaceOrArp()
    {
        // An ARP-/SSH-/HTTP-only host is minted from a MAC fingerprint with no proj_systems,
        // proj_interfaces, or proj_device_arp row — its only IP is the connect-IP the network
        // scanner recorded in proj_discovered. That must surface as the shown IP instead of "—".
        // Regression guard for the dropped-connect-IP gap (ARP entries with no IP, etc.).
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee01");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources, updated_at) "
          + "VALUES ('observer-1', '192.168.1.42', 'aabbccddee01', 'SshBannerScanner', now())"
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("192.168.1.42", items[0].Ip);
    }

    [Fact]
    public async Task Query_LastSeen_FallsBackToFingerprint_WhenNoProjSystemsRow()
    {
        // A passively-discovered device (ARP-/scanner-only) has no proj_systems row, so its
        // last_seen used to be NULL. The newest fingerprint's last_seen — stamped on every
        // resolve — must fill in instead: if we have any data about a device, we saw it.
        // Regression guard for the blank-last-seen gap.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddee02");

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.NotNull(items[0].LastSeen);
    }

    [Fact]
    public async Task Query_Sort_Descending_And_KeysetPagingAcrossSortColumn()
    {
        // Dynamic sort: order by hostname DESC, and page across that non-default sort so the
        // cursor must carry the sort key (not just hostname-asc). Regression guard for the
        // allowlist ORDER BY + generic keyset cursor.
        Guid a = await _fixture.InsertDeviceAsync("managed");
        Guid b = await _fixture.InsertDeviceAsync("managed");
        Guid c = await _fixture.InsertDeviceAsync("managed");
        await InsertSystemAsync(a, hostname: "alpha");
        await InsertSystemAsync(b, hostname: "beta");
        await InsertSystemAsync(c, hostname: "gamma");

        // Full descending order → gamma, beta, alpha.
        await ResolveIdentityAsync();

        (List<DeviceReportItem> all, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None,
            sort: "hostname",
            dir: "desc"
        );
        Assert.Equal(
            new[]
            {
                "gamma",
                "beta",
                "alpha",
            },
            all.Select(h => h.Hostname).ToArray()
        );

        // Paged descending: page 1 (limit 2) → gamma, beta + cursor.
        (List<DeviceReportItem> page1, string? cursor1) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "hostname",
            dir: "desc"
        );
        Assert.Equal(
            new[]
            {
                "gamma",
                "beta",
            },
            page1.Select(h => h.Hostname).ToArray()
        );
        Assert.NotNull(cursor1);
        Assert.True(KeysetCursor.TryDecode(cursor1, out string afterKey, out string afterDeviceId));

        // Page 2 via the cursor → alpha (paging continues in the DESC order).
        (List<DeviceReportItem> page2, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            afterKey,
            afterDeviceId,
            2,
            CancellationToken.None,
            sort: "hostname",
            dir: "desc"
        );
        Assert.Equal(
            new[]
            {
                "alpha",
            },
            page2.Select(h => h.Hostname).ToArray()
        );
    }

    [Fact]
    public async Task Query_Sort_ByIp_OrdersNumericallyNotLexically()
    {
        // IPs must sort as addresses, not strings: 192.168.1.9 < .10 < .100 (string order would
        // give .10 < .100 < .9). Regression guard for ip_sort_key.
        Guid a = await _fixture.InsertDeviceAsync("managed");
        Guid b = await _fixture.InsertDeviceAsync("managed");
        Guid c = await _fixture.InsertDeviceAsync("managed");
        await ExecuteAsync(
            $"INSERT INTO proj_systems (device, hostname, last_seen_ip, updated_at) VALUES ('{a}', 'h9', '192.168.1.9', now())"
        );
        await ExecuteAsync(
            $"INSERT INTO proj_systems (device, hostname, last_seen_ip, updated_at) VALUES ('{b}', 'h10', '192.168.1.10', now())"
        );
        await ExecuteAsync(
            $"INSERT INTO proj_systems (device, hostname, last_seen_ip, updated_at) VALUES ('{c}', 'h100', '192.168.1.100', now())"
        );

        await ResolveIdentityAsync();

        (List<DeviceReportItem> asc, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None,
            sort: "ip",
            dir: "asc"
        );
        Assert.Equal(
            new[]
            {
                "192.168.1.9",
                "192.168.1.10",
                "192.168.1.100",
            },
            asc.Select(h => h.Ip).ToArray()
        );

        // Descending is the exact reverse.
        (List<DeviceReportItem> desc, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None,
            sort: "ip",
            dir: "desc"
        );
        Assert.Equal(
            new[]
            {
                "192.168.1.100",
                "192.168.1.10",
                "192.168.1.9",
            },
            desc.Select(h => h.Ip).ToArray()
        );
    }

    [Fact]
    public async Task Query_Sources_IncludePassiveObserversForAlreadyKnownDevice()
    {
        // A host first minted by the agent (source='agent' on its MAC fingerprint) that ALSO
        // sits in the ARP cache, a DHCP lease, and a scanner sighting (SsdpScanner + MdnsScanner
        // — each becomes its own tag, not one generic 'scanner' label). The ingest anti-joins
        // never stamp 'arp'/'dhcp'/scanner-derived sources onto an already-known MAC, and the
        // single source column is last-writer-wins, so the report must DERIVE those from
        // projection presence. MAC columns are stored canonical (bare 12-hex, normalized at
        // ingest) so they join the fingerprint directly. Regression guard for the
        // missing-discovery-sources bug.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "001122334455", source: "agent");
        await ExecuteAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES ('observer-1', '192.168.1.10', '001122334455', 'eth0', 'reachable', now())"
        );
        await ExecuteAsync(
            "INSERT INTO proj_dhcp_leases (service, scope, lease, ip) "
          + "VALUES ('dhcp-1', 'lan', '001122334455', '192.168.1.10')"
        );
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources) "
          + "VALUES ('observer-1', '192.168.1.10', '001122334455', 'SsdpScanner,MdnsScanner')"
        );

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal(
            new[]
            {
                "agent",
                "arp",
                "dhcp",
                "mdns",
                "ssdp",
            },
            items[0].Sources.OrderBy(s => s, StringComparer.Ordinal).ToArray()
        );
    }

    [Fact]
    public async Task Query_Sources_UnmappedScannerClassNameFallsBackToLowercasedForm()
    {
        // A scanner class name not in the friendly-slug lookup (e.g. a newly added scanner not
        // yet wired into the map) still surfaces as its own distinct tag rather than silently
        // disappearing or collapsing into a generic label.
        Guid id = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await _fixture.InsertFingerprintAsync(id, "mac", "001122334466", source: "agent");
        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources) "
          + "VALUES ('observer-1', '192.168.1.11', '001122334466', 'BrandNewScanner')"
        );

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Contains("brandnewscanner", items[0].Sources);
    }

    [Fact]
    public async Task Query_SourceFilter_MatchesDerivedArpObservation()
    {
        // The source filter must agree with the displayed Sources column: a host known only as
        // 'agent' on its fingerprint but present in the ARP cache is still matched by source=arp,
        // while a host with no ARP presence is excluded.
        Guid arpHost = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await _fixture.InsertFingerprintAsync(arpHost, "mac", "aabbccddee01", source: "agent");
        await ExecuteAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES ('observer-1', '10.0.0.1', 'aabbccddee01', 'eth0', 'reachable', now())"
        );

        Guid noArp = await _fixture.InsertDeviceAsync(managementStatus: "managed");
        await _fixture.InsertFingerprintAsync(noArp, "mac", "aabbccddee02", source: "agent");

        (List<DeviceReportItem> items, _) = await DeviceListApi.QueryAsync(
            _fixture.DataSource,
            null,
            "arp",
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal(arpHost.ToString(), items[0].DeviceId);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ArpApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ArpApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ArpApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "proj_device_arp",
            "proj_systems",
            "proj_devices",
            "device_fingerprints",
            "devices"
        );

    [Fact]
    public async Task Query_NoArp_ReturnsEmptyList()
    {
        (List<ArpListItem> items, string? next) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_ArpEntry_ReturnsCorrectFields()
    {
        await InsertArpAsync("router-01", "192.168.1.1", "aabbccddeeff", "eth0");

        (List<ArpListItem> items, string? next) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(next);

        ArpListItem arp = items[0];
        Assert.Equal("router-01", arp.Device);
        Assert.Equal("192.168.1.1", arp.Ip);
        Assert.Equal("aabbccddeeff", arp.Mac);
        Assert.Equal("eth0", arp.Iface);
        Assert.Equal("reachable", arp.State);
    }

    [Fact]
    public async Task Query_SearchByMac_ReturnsOnlyMatchingRows()
    {
        await InsertArpAsync("router-01", "192.168.1.1", "aabbccddeeff", "eth0");
        await InsertArpAsync("router-01", "192.168.1.2", "112233445566", "eth0");

        (List<ArpListItem> items, _) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            "aabbcc",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("192.168.1.1", items[0].Ip);
    }

    [Fact]
    public async Task Query_SearchByIp_ReturnsOnlyMatchingRows()
    {
        await InsertArpAsync("router-01", "192.168.1.1", "aabbccddeeff", "eth0");
        await InsertArpAsync("router-01", "10.0.0.1", "112233445566", "eth0");

        (List<ArpListItem> items, _) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            "10.0.0",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("10.0.0.1", items[0].Ip);
    }

    [Fact]
    public async Task Query_ArpEntry_ResolvesDeviceWhenFingerprintMatches()
    {
        Guid devId = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(devId, "mac", "aabbccddeeff");

        await InsertArpAsync("router-01", "192.168.1.1", "aabbccddeeff", "eth0");

        (List<ArpListItem> items, _) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal(devId.ToString(), items[0].ResolvedDeviceId);
    }

    [Fact]
    public async Task Query_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        await InsertArpAsync("router-01", "10.0.0.1", "aaaaaaaaaaaa", "eth0");
        await InsertArpAsync("router-01", "10.0.0.2", "bbbbbbbbbbbb", "eth0");
        await InsertArpAsync("router-01", "10.0.0.3", "cccccccccccc", "eth0");

        (List<ArpListItem> page1, string? cursor1) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<ArpListItem> page2, string? cursor2) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("10.0.0.3", page2[0].Ip);
    }

    [Fact]
    public async Task Query_SortByMacDescending_OrdersAndPaginatesByMac()
    {
        await InsertArpAsync("router-01", "10.0.0.1", "aaaaaaaaaaaa", "eth0");
        await InsertArpAsync("router-01", "10.0.0.2", "cccccccccccc", "eth0");
        await InsertArpAsync("router-01", "10.0.0.3", "bbbbbbbbbbbb", "eth0");

        (List<ArpListItem> page1, string? cursor1) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "mac",
            dir: "desc"
        );

        Assert.Equal(2, page1.Count);
        Assert.Equal("cccccccccccc", page1[0].Mac);
        Assert.Equal("bbbbbbbbbbbb", page1[1].Mac);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<ArpListItem> page2, string? cursor2) = await ArpApi.QueryAsync(
            _fixture.DataSource,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None,
            sort: "mac",
            dir: "desc"
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("aaaaaaaaaaaa", page2[0].Mac);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertArpAsync(
        string device,
        string ip,
        string mac,
        string iface
    )
    {
        const string sql = """
            INSERT INTO proj_device_arp (device, arp, mac, iface, state)
            VALUES (@device, @ip, @mac, @iface, @state)
            ON CONFLICT (device, arp) DO UPDATE SET mac = EXCLUDED.mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", mac);
        cmd.Parameters.AddWithValue("iface", iface);
        // ARP entries are only seeded in the "reachable" state; no test varies it.
        cmd.Parameters.AddWithValue("state", "reachable");
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PortsApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class PortsApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public PortsApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("proj_ports", "proj_systems");

    [Fact]
    public async Task Query_NoPorts_ReturnsEmptyList()
    {
        (List<PortListItem> items, string? next) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_PortEntry_ReturnsCorrectFields()
    {
        await InsertPortAsync("device-a", "tcp:0.0.0.0:22", "tcp", "0.0.0.0", 22, "sshd", 1001);

        (List<PortListItem> items, string? next) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(next);

        PortListItem p = items[0];
        Assert.Equal("device-a", p.Device);
        Assert.Equal("tcp", p.Protocol);
        Assert.Equal("0.0.0.0", p.Address);
        Assert.Equal(22, p.Port);
        Assert.Equal("sshd", p.ProcessName);
        Assert.Equal(1001L, p.Pid);
    }

    [Fact]
    public async Task Query_FilterByPortNumber_ReturnsOnlyMatchingPorts()
    {
        await InsertPortAsync("device-a", "tcp:0.0.0.0:22", "tcp", "0.0.0.0", 22, "sshd", 100);
        await InsertPortAsync("device-a", "tcp:0.0.0.0:443", "tcp", "0.0.0.0", 443, "nginx", 200);

        (List<PortListItem> items, _) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            22,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal(22, items[0].Port);
    }

    [Fact]
    public async Task Query_FilterByProtocol_ReturnsOnlyMatchingPorts()
    {
        await InsertPortAsync("device-a", "tcp:0.0.0.0:22", "tcp", "0.0.0.0", 22, "sshd", 100);
        await InsertPortAsync("device-a", "udp:0.0.0.0:53", "udp", "0.0.0.0", 53, "named", 200);

        (List<PortListItem> items, _) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            "udp",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("udp", items[0].Protocol);
    }

    [Fact]
    public async Task Query_PortJoinsHostname()
    {
        await InsertSystemAsync("device-b", "myhost");
        await InsertPortAsync("device-b", "tcp:0.0.0.0:80", "tcp", "0.0.0.0", 80, "nginx", 300);

        (List<PortListItem> items, _) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("myhost", items[0].Hostname);
    }

    [Fact]
    public async Task Query_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        // Use listeningport keys that sort predictably as strings.
        await InsertPortAsync("device-a", "tcp:0.0.0.0:aport", "tcp", "0.0.0.0", 22, "sshd", 1);
        await InsertPortAsync("device-a", "tcp:0.0.0.0:bport", "tcp", "0.0.0.0", 80, "nginx", 2);
        await InsertPortAsync("device-a", "tcp:0.0.0.0:cport", "tcp", "0.0.0.0", 443, "nginx", 3);

        (List<PortListItem> page1, string? cursor1) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);
        // page1 has aport (22) and bport (80) — sorted ASC by listeningport.
        Assert.Equal(22, page1[0].Port);
        Assert.Equal(80, page1[1].Port);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<PortListItem> page2, string? cursor2) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal(443, page2[0].Port);
    }

    [Fact]
    public async Task Query_SortByPortDescending_OrdersAndPaginatesByPort()
    {
        await InsertPortAsync("device-a", "tcp:0.0.0.0:a", "tcp", "0.0.0.0", 22, "sshd", 1);
        await InsertPortAsync("device-b", "tcp:0.0.0.0:b", "tcp", "0.0.0.0", 8080, "app", 2);
        await InsertPortAsync("device-c", "tcp:0.0.0.0:c", "tcp", "0.0.0.0", 443, "nginx", 3);

        (List<PortListItem> page1, string? cursor1) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "port",
            dir: "desc"
        );

        Assert.Equal(2, page1.Count);
        Assert.Equal(8080, page1[0].Port);
        Assert.Equal(443, page1[1].Port);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<PortListItem> page2, string? cursor2) = await PortsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None,
            sort: "port",
            dir: "desc"
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal(22, page2[0].Port);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertPortAsync(
        string device,
        string listeningPort,
        string? protocol,
        string? address,
        int? port,
        string? processName,
        long? pid
    )
    {
        const string sql = """
            INSERT INTO proj_ports (device, listeningport, protocol, address, port, process_name, pid)
            VALUES (@device, @listeningport, @protocol, @address, @port, @processName, @pid)
            ON CONFLICT (device, listeningport) DO UPDATE
              SET protocol = EXCLUDED.protocol,
                  port = EXCLUDED.port
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("listeningport", listeningPort);
        cmd.Parameters.AddWithValue("protocol", (object?)protocol ?? DBNull.Value);
        cmd.Parameters.AddWithValue("address", (object?)address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("port", (object?)port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("processName", (object?)processName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pid", (object?)pid ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ContainersApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ContainersApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ContainersApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("proj_containers", "proj_systems");

    [Fact]
    public async Task Query_NoContainers_ReturnsEmptyList()
    {
        (List<ContainerListItem> items, string? next) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_ContainerEntry_ReturnsCorrectFields()
    {
        await InsertContainerAsync(
            device: "docker-host-1",
            container: "abc123def456",
            name: "web",
            image: "nginx:latest",
            state: "running",
            health: "healthy",
            cpuPct: 1.5,
            memUsageBytes: 104857600,
            restartCount: 0,
            composeProject: "myapp",
            composeService: "web"
        );

        (List<ContainerListItem> items, _) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        ContainerListItem c = items[0];
        Assert.Equal("docker-host-1", c.Device);
        Assert.Equal("abc123def456", c.Container);
        Assert.Equal("web", c.Name);
        Assert.Equal("nginx:latest", c.Image);
        Assert.Equal("running", c.State);
        Assert.Equal("healthy", c.Health);
        Assert.Equal(1.5, c.CpuPct);
        Assert.Equal(104857600L, c.MemUsageBytes);
        Assert.Equal("myapp", c.ComposeProject);
        Assert.Equal("web", c.ComposeService);
    }

    [Fact]
    public async Task Query_FilterByState_ReturnsOnlyMatchingContainers()
    {
        await InsertContainerAsync("host-1", "running-container", state: "running");
        await InsertContainerAsync("host-1", "exited-container", state: "exited");

        (List<ContainerListItem> items, _) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            "running",
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("running", items[0].State);
    }

    [Fact]
    public async Task Query_FilterByImage_ReturnsOnlyMatchingContainers()
    {
        await InsertContainerAsync("host-1", "nginx-c1", image: "nginx:1.24");
        await InsertContainerAsync("host-1", "redis-c1", image: "redis:7");

        (List<ContainerListItem> items, _) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            "nginx",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("nginx-c1", items[0].Container);
    }

    [Fact]
    public async Task Query_ContainerJoinsHostname()
    {
        await InsertSystemAsync("docker-host-1", "my-docker-server");
        await InsertContainerAsync("docker-host-1", "app-container");

        (List<ContainerListItem> items, _) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("my-docker-server", items[0].Hostname);
    }

    [Fact]
    public async Task Query_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        await InsertContainerAsync("host-1", "aaa-container");
        await InsertContainerAsync("host-1", "bbb-container");
        await InsertContainerAsync("host-1", "ccc-container");

        (List<ContainerListItem> page1, string? cursor1) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<ContainerListItem> page2, string? cursor2) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("ccc-container", page2[0].Container);
    }

    [Fact]
    public async Task Query_SortByStateDescending_OrdersAndPaginatesByState()
    {
        await InsertContainerAsync("host-1", "c1", state: "exited");
        await InsertContainerAsync("host-2", "c2", state: "running");
        await InsertContainerAsync("host-3", "c3", state: "paused");

        (List<ContainerListItem> page1, string? cursor1) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "state",
            dir: "desc"
        );

        Assert.Equal(2, page1.Count);
        Assert.Equal("running", page1[0].State);
        Assert.Equal("paused", page1[1].State);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<ContainerListItem> page2, string? cursor2) = await ContainersApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None,
            sort: "state",
            dir: "desc"
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("exited", page2[0].State);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertContainerAsync(
        string device,
        string container,
        string? name = null,
        string? image = null,
        string? state = null,
        string? health = null,
        double? cpuPct = null,
        long? memUsageBytes = null,
        long? restartCount = null,
        string? composeProject = null,
        string? composeService = null
    )
    {
        const string sql = """
            INSERT INTO proj_containers (device, container, name, image, state, health, cpu_pct, mem_usage_bytes, restart_count, compose_project, compose_service)
            VALUES (@device, @container, @name, @image, @state, @health, @cpuPct, @memUsageBytes, @restartCount, @composeProject, @composeService)
            ON CONFLICT (device, container) DO UPDATE
              SET state = EXCLUDED.state, image = EXCLUDED.image
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("container", container);
        cmd.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("image", (object?)image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", (object?)state ?? DBNull.Value);
        cmd.Parameters.AddWithValue("health", (object?)health ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cpuPct", (object?)cpuPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("memUsageBytes", (object?)memUsageBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("restartCount", (object?)restartCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("composeProject", (object?)composeProject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("composeService", (object?)composeService ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ChangesApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ChangesApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ChangesApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("facts_history", "proj_systems");

    [Fact]
    public async Task Query_NoChanges_ReturnsEmptyList()
    {
        (List<ChangeListItem> items, string? next) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_StringChange_ReturnsCorrectFields()
    {
        string devId = Guid.NewGuid().ToString();
        DateTimeOffset ts = DateTimeOffset.UtcNow.AddMinutes(-5);
        string factId = Guid.NewGuid().ToString();

        await InsertHistoryAsync(
            id: factId,
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devId}\"}}",
            kind: 1, // String
            valueStr: "my-hostname",
            collectedAt: ts
        );

        (List<ChangeListItem> items, _) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        ChangeListItem c = items[0];
        Assert.Equal(factId, c.Id);
        Assert.Equal("Device.OS.Hostname", c.AttributePath);
        Assert.Equal("my-hostname", c.Value);
    }

    [Fact]
    public async Task Query_FilterByDeviceId_ReturnsOnlyMatchingChanges()
    {
        string devA = Guid.NewGuid().ToString();
        string devB = Guid.NewGuid().ToString();

        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devA}\"}}",
            kind: 1,
            valueStr: "host-a",
            collectedAt: DateTimeOffset.UtcNow.AddMinutes(-10)
        );
        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devB}\"}}",
            kind: 1,
            valueStr: "host-b",
            collectedAt: DateTimeOffset.UtcNow.AddMinutes(-10)
        );

        (List<ChangeListItem> items, _) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            devA,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("host-a", items[0].Value);
    }

    [Fact]
    public async Task Query_FilterBySince_ExcludesOlderChanges()
    {
        string devId = Guid.NewGuid().ToString();

        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devId}\"}}",
            kind: 1,
            valueStr: "old-host",
            collectedAt: DateTimeOffset.UtcNow.AddHours(-48) // older than 24h
        );
        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devId}\"}}",
            kind: 1,
            valueStr: "new-host",
            collectedAt: DateTimeOffset.UtcNow.AddMinutes(-30) // within 1h
        );

        (List<ChangeListItem> items, _) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            "1h",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("new-host", items[0].Value);
    }

    [Fact]
    public async Task Query_LongKind_RendersAsInteger()
    {
        string devId = Guid.NewGuid().ToString();
        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.Memory.TotalBytes",
            keyValues: $"{{\"Device\":\"{devId}\"}}",
            kind: 2, // Long
            valueLong: 8589934592L,
            collectedAt: DateTimeOffset.UtcNow.AddMinutes(-1)
        );

        (List<ChangeListItem> items, _) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("8589934592", items[0].Value);
    }

    [Fact]
    public async Task Query_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        string devId = Guid.NewGuid().ToString();
        // Insert 3 facts at distinct timestamps (oldest first).
        DateTimeOffset t1 = DateTimeOffset.UtcNow.AddMinutes(-30);
        DateTimeOffset t2 = DateTimeOffset.UtcNow.AddMinutes(-20);
        DateTimeOffset t3 = DateTimeOffset.UtcNow.AddMinutes(-10);

        // changes are returned newest-first (DESC), so t3, t2, t1.
        await InsertHistoryAsync(
            Guid.NewGuid().ToString(),
            "Device.A",
            $"{{\"Device\":\"{devId}\"}}",
            1,
            valueStr: "val-a",
            collectedAt: t1
        );
        await InsertHistoryAsync(
            Guid.NewGuid().ToString(),
            "Device.B",
            $"{{\"Device\":\"{devId}\"}}",
            1,
            valueStr: "val-b",
            collectedAt: t2
        );
        await InsertHistoryAsync(
            Guid.NewGuid().ToString(),
            "Device.C",
            $"{{\"Device\":\"{devId}\"}}",
            1,
            valueStr: "val-c",
            collectedAt: t3
        );

        (List<ChangeListItem> page1, string? cursor1) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);
        // Page 1 should be the two newest: val-c, val-b
        Assert.Equal("val-c", page1[0].Value);
        Assert.Equal("val-b", page1[1].Value);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 2, out string[] parts));
        Assert.True(long.TryParse(parts[0], out long ticks));
        DateTimeOffset afterTs = new(ticks, TimeSpan.Zero);

        (List<ChangeListItem> page2, string? cursor2) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            afterTs,
            parts[1],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("val-a", page2[0].Value);
    }

    [Fact]
    public async Task Query_ChangesJoinsHostname_WhenSystemExists()
    {
        string devId = Guid.NewGuid().ToString();
        await InsertSystemAsync(devId, "change-monitor");
        await InsertHistoryAsync(
            id: Guid.NewGuid().ToString(),
            attributePath: "Device.OS.Hostname",
            keyValues: $"{{\"Device\":\"{devId}\"}}",
            kind: 1,
            valueStr: "change-monitor",
            collectedAt: DateTimeOffset.UtcNow.AddMinutes(-5)
        );

        (List<ChangeListItem> items, _) = await ChangesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("change-monitor", items[0].Hostname);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertHistoryAsync(
        string id,
        string attributePath,
        string keyValues,
        short kind,
        string? valueStr = null,
        long? valueLong = null,
        double? valueDouble = null,
        DateTimeOffset? collectedAt = null
    )
    {
        const string sql = """
            INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, value_long, value_double, collected_at)
            VALUES (@id, @path, @keys::jsonb, @kind, @valueStr, @valueLong, @valueDouble, @collectedAt)
            ON CONFLICT (id, collected_at) DO NOTHING
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("path", attributePath);
        cmd.Parameters.AddWithValue("keys", keyValues);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("valueStr", (object?)valueStr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("valueLong", (object?)valueLong ?? DBNull.Value);
        cmd.Parameters.AddWithValue("valueDouble", (object?)valueDouble ?? DBNull.Value);
        cmd.Parameters.AddWithValue("collectedAt", collectedAt ?? DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StorageApi — disks
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class StorageApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public StorageApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync("proj_disks", "proj_filesystems", "proj_systems");

    // ── Disks ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryDisks_NoDisks_ReturnsEmptyList()
    {
        (List<DiskListItem> items, string? next) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task QueryDisks_DiskEntry_ReturnsCorrectFields()
    {
        await InsertDiskAsync(
            device: "nas-1",
            disk: "sda",
            name: "sda",
            model: "WD Red 4TB",
            type: "HDD",
            smartHealth: "PASSED",
            smartTempC: 38.0,
            smartWearPct: 12.0,
            sizeBytes: 4000787030016L
        );

        (List<DiskListItem> items, _) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        DiskListItem d = items[0];
        Assert.Equal("nas-1", d.Device);
        Assert.Equal("sda", d.Disk);
        Assert.Equal("WD Red 4TB", d.Model);
        Assert.Equal("HDD", d.Type);
        Assert.Equal("PASSED", d.SmartHealth);
        Assert.Equal(38.0, d.SmartTempC);
        Assert.Equal(4000787030016L, d.SizeBytes);
    }

    [Fact]
    public async Task QueryDisks_DiskJoinsHostname()
    {
        await InsertSystemAsync("storage-host", "file-server");
        await InsertDiskAsync("storage-host", "sda");

        (List<DiskListItem> items, _) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("file-server", items[0].Hostname);
    }

    [Fact]
    public async Task QueryDisks_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        await InsertDiskAsync("nas-1", "sda");
        await InsertDiskAsync("nas-1", "sdb");
        await InsertDiskAsync("nas-1", "sdc");

        (List<DiskListItem> page1, string? cursor1) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 2, out string[] parts));

        (List<DiskListItem> page2, string? cursor2) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            null,
            parts[0],
            parts[1],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("sdc", page2[0].Disk);
    }

    [Fact]
    public async Task QueryDisks_Search_MatchesModelCaseInsensitive()
    {
        await InsertDiskAsync("nas-1", "sda", model: "WD Red 4TB");
        await InsertDiskAsync("nas-2", "sda", model: "Samsung 870 EVO");

        (List<DiskListItem> items, _) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            "samsung",
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("nas-2", items[0].Device);
    }

    [Fact]
    public async Task QueryDisks_Search_MatchesJoinedHostname()
    {
        await InsertSystemAsync("nas-1", "file-server");
        await InsertDiskAsync("nas-1", "sda");
        await InsertDiskAsync("other", "sda");

        (List<DiskListItem> items, _) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            "file-serv",
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("nas-1", items[0].Device);
    }

    [Fact]
    public async Task QueryDisks_Search_BlankIsIgnored()
    {
        await InsertDiskAsync("nas-1", "sda");
        await InsertDiskAsync("nas-2", "sda");

        (List<DiskListItem> items, _) = await StorageApi.QueryDisksAsync(
            _fixture.DataSource,
            "   ",
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Equal(2, items.Count);
    }

    // ── Filesystems ───────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryFilesystems_NoFilesystems_ReturnsEmptyList()
    {
        (List<FilesystemListItem> items, string? next) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task QueryFilesystems_FilesystemEntry_ReturnsCorrectFields()
    {
        await InsertFilesystemAsync(
            device: "nas-1",
            filesystem: "/",
            fsType: "ext4",
            totalBytes: 107374182400L,
            usedBytes: 53687091200L,
            freeBytes: 53687091200L,
            usedPct: 50.0
        );

        (List<FilesystemListItem> items, _) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        FilesystemListItem f = items[0];
        Assert.Equal("nas-1", f.Device);
        Assert.Equal("/", f.Filesystem);
        Assert.Equal("ext4", f.FsType);
        Assert.Equal(107374182400L, f.TotalBytes);
        Assert.Equal(50.0, f.UsedPct);
    }

    [Fact]
    public async Task QueryFilesystems_Pagination_ReturnsNextCursor_ThenSecondPage()
    {
        await InsertFilesystemAsync("nas-1", "/boot");
        await InsertFilesystemAsync("nas-1", "/data");
        await InsertFilesystemAsync("nas-1", "/home");

        (List<FilesystemListItem> page1, string? cursor1) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            2,
            CancellationToken.None
        );

        Assert.Equal(2, page1.Count);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 2, out string[] parts));

        (List<FilesystemListItem> page2, string? cursor2) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            null,
            parts[0],
            parts[1],
            2,
            CancellationToken.None
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("/home", page2[0].Filesystem);
    }

    [Fact]
    public async Task QueryFilesystems_Search_MatchesMountAndType()
    {
        await InsertFilesystemAsync("nas-1", "/data", fsType: "ext4");
        await InsertFilesystemAsync("nas-1", "/boot", fsType: "vfat");

        (List<FilesystemListItem> byMount, _) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            "/data",
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byMount);
        Assert.Equal("/data", byMount[0].Filesystem);

        (List<FilesystemListItem> byType, _) = await StorageApi.QueryFilesystemsAsync(
            _fixture.DataSource,
            "vfat",
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byType);
        Assert.Equal("/boot", byType[0].Filesystem);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertDiskAsync(
        string device,
        string disk,
        string? name = null,
        string? model = null,
        string? type = null,
        string? smartHealth = null,
        double? smartTempC = null,
        double? smartWearPct = null,
        long? sizeBytes = null
    )
    {
        const string sql = """
            INSERT INTO proj_disks (device, disk, name, model, type, smart_health, smart_temp_c, smart_wear_pct, size_bytes)
            VALUES (@device, @disk, @name, @model, @type, @smartHealth, @smartTempC, @smartWearPct, @sizeBytes)
            ON CONFLICT (device, disk) DO UPDATE SET model = EXCLUDED.model
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("disk", disk);
        cmd.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("type", (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("smartHealth", (object?)smartHealth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("smartTempC", (object?)smartTempC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("smartWearPct", (object?)smartWearPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sizeBytes", (object?)sizeBytes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertFilesystemAsync(
        string device,
        string filesystem,
        string? fsType = null,
        long? totalBytes = null,
        long? usedBytes = null,
        long? freeBytes = null,
        double? usedPct = null
    )
    {
        const string sql = """
            INSERT INTO proj_filesystems (device, filesystem, fs_type, total_bytes, used_bytes, free_bytes, used_pct)
            VALUES (@device, @filesystem, @fsType, @totalBytes, @usedBytes, @freeBytes, @usedPct)
            ON CONFLICT (device, filesystem) DO UPDATE SET fs_type = EXCLUDED.fs_type
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("filesystem", filesystem);
        cmd.Parameters.AddWithValue("fsType", (object?)fsType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("totalBytes", (object?)totalBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("usedBytes", (object?)usedBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("freeBytes", (object?)freeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("usedPct", (object?)usedPct ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SubnetsApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class SubnetsApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public SubnetsApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "proj_interfaces",
            "proj_dhcp_scopes",
            "proj_device_arp",
            "proj_device_routes",
            "proj_dhcp_leases",
            "proj_discovered",
            "proj_docker_networks",
            "proj_systems",
            "proj_devices",
            "device_fingerprints",
            "devices"
        );

    [Fact]
    public async Task Query_NoData_ReturnsEmptyList()
    {
        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Query_InterfaceOnly_DerivesSubnetFromCidr()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.10/24");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.1.0/24", subnet.Cidr);
        Assert.Contains("I", subnet.Sources);
    }

    [Fact]
    public async Task Query_InterfaceBareIpWithPrefixLength_SynthesizesCidr()
    {
        // Google Wifi/OnHub collectors emit a bare IP for proj_interfaces.ipv4 (that bare-IP
        // meaning is an exact-match join key elsewhere — DiscoveryMaterializer's MAC
        // reconstruction) plus a separately captured prefix length. An isolated interface with
        // no other peer covering its subnet (e.g. the guest network's own AP interface) must
        // still synthesize a subnet from ipv4 + ipv4_prefix_length, not be silently dropped.
        await InsertInterfaceAsync("onhub-01", "br-guest", "10.0.0.1", ipv4PrefixLength: 24);

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("10.0.0.0/24", subnet.Cidr);
        Assert.Contains("I", subnet.Sources);
    }

    [Fact]
    public async Task Query_InterfaceBareIpWithoutPrefixLength_Excluded()
    {
        // Without a prefix length, a bare IP can't produce a subnet — must not synthesize a
        // bogus /32.
        await InsertInterfaceAsync("onhub-01", "br-guest", "10.0.0.1");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Query_DhcpScopeOnly_DerivesSubnetWithNameAndGateway()
    {
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.1.0/24", subnet.Cidr);
        Assert.Equal("LAN", subnet.Name);
        Assert.Equal("192.168.1.1", subnet.Gateway);
        Assert.Contains("D", subnet.Sources);
    }

    [Fact]
    public async Task Query_DisabledDhcpScope_Excluded()
    {
        await InsertDhcpScopeAsync(
            "dns-01",
            "Old",
            startAddress: "10.0.0.1",
            endAddress: "10.0.0.254",
            subnetMask: "255.255.255.0",
            gateway: "10.0.0.1",
            enabled: false
        );

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Query_InterfaceAndDhcpScope_MergeIntoOneSubnet()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.1/24");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.1.0/24", subnet.Cidr);
        Assert.Equal("LAN", subnet.Name);
        Assert.Contains("I", subnet.Sources);
        Assert.Contains("D", subnet.Sources);
    }

    [Fact]
    public async Task Query_HostCount_CountsOnlyIpsWithinSubnet()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.1/24");
        await InsertArpAsync("router-01", "192.168.1.50", "aabbccddeeff", "eth0");
        await InsertArpAsync("router-01", "192.168.1.51", "112233445566", "eth0");
        await InsertArpAsync("router-01", "10.0.0.5", "aabbaabbaabb", "eth0"); // different subnet

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        // .1 (the interface itself) + .50 + .51 = 3
        Assert.Equal(3, subnet.HostCount);
    }

    [Fact]
    public async Task Query_Gateway_ResolvesToKnownDevice()
    {
        Guid devId = await _fixture.InsertDeviceAsync("managed");
        await _fixture.InsertFingerprintAsync(devId, "mac", "aabbccddeeff");
        await InsertSystemAsync(devId.ToString(), "gateway-router");

        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );
        await InsertArpAsync("some-agent", "192.168.1.1", "aabbccddeeff", "eth0");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal(devId.ToString(), subnet.GatewayDeviceId);
        Assert.Equal("gateway-router", subnet.GatewayHostname);
    }

    [Fact]
    public async Task Query_SearchByName_ReturnsOnlyMatchingSubnets()
    {
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );
        await InsertDhcpScopeAsync(
            "dns-01",
            "IoT",
            startAddress: "192.168.10.100",
            endAddress: "192.168.10.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.10.1"
        );

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, "IoT", CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.10.0/24", subnet.Cidr);
    }

    [Fact]
    public async Task GetDetail_MatchingNetwork_ReturnsInterfacesAndScope()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.1/24");
        await InsertSystemAsync("router-01", "router-hostname");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );

        SubnetDetail? detail = await SubnetsApi.GetDetailAsync(
            _fixture.DataSource,
            IPNetwork.Parse("192.168.1.0/24"),
            CancellationToken.None
        );

        Assert.NotNull(detail);
        Assert.Equal("192.168.1.0/24", detail.Cidr);
        Assert.Equal("LAN", detail.Name);
        Assert.Equal("192.168.1.100", detail.DhcpStart);
        SubnetInterface iface = Assert.Single(detail.Interfaces);
        Assert.Equal("router-hostname", iface.Hostname);
        Assert.Equal("192.168.1.1", iface.Ip);
    }

    [Fact]
    public async Task GetDetail_NoMatchingSubnet_ReturnsNull()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.1/24");

        SubnetDetail? detail = await SubnetsApi.GetDetailAsync(
            _fixture.DataSource,
            IPNetwork.Parse("10.0.0.0/24"),
            CancellationToken.None
        );

        Assert.Null(detail);
    }

    [Fact]
    public async Task Query_ConnectedRoute_DerivesSubnetNotSeenViaInterfaceOrDhcp()
    {
        // A secondary/aliased subnet a router forwards for, with no interface row and no DHCP
        // scope of its own — only a connected route reveals it exists.
        await InsertRouteAsync("router-01", "192.168.50.0/24");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.50.0/24", subnet.Cidr);
        Assert.Contains("R", subnet.Sources);
    }

    [Fact]
    public async Task Query_DefaultRouteGateway_FillsGatewayWhenDhcpAbsent()
    {
        await InsertInterfaceAsync("router-01", "eth0", "192.168.1.5/24");
        await InsertRouteAsync("nas-01", "0.0.0.0/0", gateway: "192.168.1.1");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.1.1", subnet.Gateway);
        Assert.Contains("R", subnet.Sources);
    }

    [Fact]
    public async Task Query_DefaultRouteGateway_DoesNotOverrideDhcpGateway()
    {
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );
        await InsertRouteAsync("nas-01", "0.0.0.0/0", gateway: "192.168.1.254");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("192.168.1.1", subnet.Gateway);
    }

    [Fact]
    public async Task GetGraph_NoData_ReturnsEmptyGraph()
    {
        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task GetGraph_RouterWithTwoSubnets_DrawsEdgeToEach()
    {
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertInterfaceAsync("udm", "eth1", "192.168.10.1/24");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.Equal(3, graph.Nodes.Count); // 1 router + 2 subnets
        Assert.Equal(2, graph.Edges.Count);
        SubnetGraphNode router = Assert.Single(graph.Nodes, n => n.Kind == "router");
        Assert.All(graph.Edges, e => Assert.Equal(router.Id, e.FromId));
    }

    [Fact]
    public async Task GetGraph_DefaultRouteOffKnownNetwork_AddsInternetNode()
    {
        await InsertInterfaceAsync("nas-01", "eth0", "192.168.1.5/24");
        await InsertSystemAsync("nas-01", "nas-01");
        // Gateway is outside every known subnet -> genuinely leads off-network.
        await InsertRouteAsync("nas-01", "0.0.0.0/0", gateway: "203.0.113.1");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        SubnetGraphNode internet = Assert.Single(graph.Nodes, n => n.Kind == "internet");
        Assert.Contains(graph.Edges, e => e.FromId == internet.Id);
    }

    [Fact]
    public async Task GetGraph_DefaultRouteInsideKnownSubnet_NoInternetNode()
    {
        await InsertInterfaceAsync("nas-01", "eth0", "192.168.1.5/24");
        // Gateway resolves to the NAS's own subnet — a normal internal hop, not an off-network edge.
        await InsertRouteAsync("nas-01", "0.0.0.0/0", gateway: "192.168.1.1");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == "internet");
    }

    [Fact]
    public async Task GetGraph_DefaultRouteOffKnownNetwork_LabelsEdgeWithWanInterface()
    {
        await InsertInterfaceAsync("nas-01", "eth0", "192.168.1.5/24");
        await InsertRouteAsync("nas-01", "0.0.0.0/0", gateway: "203.0.113.1", iface: "eth1");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        SubnetGraphNode internet = Assert.Single(graph.Nodes, n => n.Kind == "internet");
        SubnetGraphEdge edge = Assert.Single(graph.Edges, e => e.FromId == internet.Id);
        Assert.Equal("eth1", edge.Via);
    }

    [Fact]
    public async Task GetGraph_RouterGatewayInStubSubnet_FlagsWanSubnet()
    {
        // udm is a recognized router (LAN's resolved DHCP gateway) whose own default route leads
        // to a gateway sitting inside a second, DHCP-less, near-empty subnet — the ISP-assigned
        // uplink segment made locally visible, as opposed to the common case where nothing on
        // that subnet is observable at all (see the Internet-node tests above).
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertInterfaceAsync("udm", "eth1", "203.0.113.5/30");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );
        await InsertRouteAsync("udm", "0.0.0.0/0", gateway: "203.0.113.6", iface: "eth1");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == "internet"); // gateway is inside a known subnet
        SubnetGraphNode wanSubnet = Assert.Single(graph.Nodes, n => n.Kind == "wan-subnet");
        Assert.Contains("203.0.113.4/30", wanSubnet.Label);
        // The ordinary LAN subnet stays an ordinary "subnet" — only the uplink segment is retagged.
        Assert.Contains(graph.Nodes, n => n.Kind == "subnet" && n.Label.Contains("192.168.1"));
    }

    [Fact]
    public async Task GetGraph_ClientGatewayInOrdinaryLanSubnet_DoesNotFlagWanSubnet()
    {
        // A client's default route into the ordinary home LAN must never be mistaken for a WAN
        // uplink just because its gateway lands inside a known subnet — only a device this graph
        // already recognizes as a router can nominate a subnet as WAN-adjacent.
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertInterfaceAsync("laptop", "eth0", "192.168.1.50/24");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );
        await InsertRouteAsync("laptop", "0.0.0.0/0", gateway: "192.168.1.1");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == "wan-subnet");
    }

    [Fact]
    public async Task Query_GatewayMatchesKnownInterface_ResolvesWithoutArp()
    {
        // udm's own interface IP is LAN's DHCP gateway — no ARP/fingerprint row exists for it at
        // all, but it's still definitively resolvable because udm self-reports that exact address.
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertSystemAsync("udm", "udm-pro");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        SubnetListItem subnet = Assert.Single(items);
        Assert.Equal("udm", subnet.GatewayDeviceId);
        Assert.Equal("udm-pro", subnet.GatewayHostname);
    }

    [Fact]
    public async Task GetGraph_RouterResolvedViaInterfaceAndGateway_MergesIntoOneNode()
    {
        // udm has interfaces in both LAN and IoT (the multi-subnet-interface rule) AND is LAN's
        // resolved DHCP gateway (the gateway-resolution rule) — both must key to the SAME router
        // node, not split into two nodes for one physical device.
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertInterfaceAsync("udm", "eth1", "192.168.10.1/24");
        await InsertSystemAsync("udm", "udm-pro");
        await InsertDhcpScopeAsync(
            "dns-01",
            "LAN",
            startAddress: "192.168.1.100",
            endAddress: "192.168.1.200",
            subnetMask: "255.255.255.0",
            gateway: "192.168.1.1"
        );

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.Equal(3, graph.Nodes.Count); // 1 router + 2 subnets, NOT 4
        SubnetGraphNode router = Assert.Single(graph.Nodes, n => n.Kind == "router");
        Assert.Equal("udm-pro (192.168.1.1)", router.Label); // hostname + gateway IP
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, e => Assert.Equal(router.Id, e.FromId));
    }

    // ── Track 1: host-local Docker bridge keying ────────────────────────────────

    [Fact]
    public async Task GetGraph_TwoHostsSameDockerBridge_KeptSeparatePerHost()
    {
        // Both hosts run Docker, so both have docker0 = 172.17.0.0/16 (host-local NAT, driver
        // bridge) plus a real LAN interface. The identical bridge CIDR must NOT merge into one
        // shared node — that would falsely chain the two hosts through Docker's internal network.
        await InsertInterfaceAsync("hostA", "eth0", "192.168.1.10/24");
        await InsertInterfaceAsync("hostA", "docker0", "172.17.0.1/16");
        await InsertDockerNetworkAsync("hostA", "172.17.0.0/16", "bridge");
        await InsertInterfaceAsync("hostB", "eth0", "192.168.1.20/24");
        await InsertInterfaceAsync("hostB", "docker0", "172.17.0.1/16");
        await InsertDockerNetworkAsync("hostB", "172.17.0.0/16", "bridge");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        // Two distinct 172.17.0.0/16 subnet nodes (one per host), not one shared node.
        List<SubnetGraphNode> bridgeNodes = graph.Nodes
            .Where(n => n.Kind == "subnet" && n.Label.Contains("172.17.0.0/16"))
            .ToList();
        Assert.Equal(2, bridgeNodes.Count);
        Assert.Equal(2, bridgeNodes.Select(n => n.Id).Distinct().Count());

        // Each bridge node attaches to exactly one router (its owning host) — no node bridges both.
        foreach (SubnetGraphNode bridge in bridgeNodes)
        {
            Assert.Single(graph.Edges, e => e.ToId == bridge.Id);
        }
    }

    [Fact]
    public async Task GetGraph_MacvlanSameCidrAcrossHosts_MergesGlobally()
    {
        // macvlan holds real, routable LAN IPs — the same CIDR on two hosts IS the same subnet
        // and must merge into one node. Only driver=bridge is host-local; macvlan is not flagged.
        await InsertInterfaceAsync("hostA", "eth0", "10.10.0.5/24");
        await InsertDockerNetworkAsync("hostA", "10.10.0.0/24", "macvlan", name: "pub");
        await InsertInterfaceAsync("hostB", "eth0", "10.10.0.6/24");
        await InsertDockerNetworkAsync("hostB", "10.10.0.0/24", "macvlan", name: "pub");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        SubnetGraphNode subnet = Assert.Single(graph.Nodes, n => n.Kind == "subnet");
        Assert.Contains("10.10.0.0/24", subnet.Label);
    }

    [Fact]
    public async Task Query_TwoHostsSameDockerBridge_ListsPerHostRows()
    {
        // The list is fed by the same aggregation, so host-local bridges surface as one row per
        // host (documents the per-host keying decision — see docs/plans/l3-topology.md §8).
        await InsertInterfaceAsync("hostA", "docker0", "172.17.0.1/16");
        await InsertDockerNetworkAsync("hostA", "172.17.0.0/16", "bridge");
        await InsertInterfaceAsync("hostB", "docker0", "172.17.0.1/16");
        await InsertDockerNetworkAsync("hostB", "172.17.0.0/16", "bridge");

        List<SubnetListItem> items = await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None);

        Assert.Equal(2, items.Count(i => i.Cidr == "172.17.0.0/16"));
    }

    // ── Track 2: VPN/overlay clouds ─────────────────────────────────────────────

    [Fact]
    public async Task GetGraph_TailscaleInterface_AddsVpnCloud()
    {
        await InsertInterfaceAsync("hostA", "eth0", "192.168.1.10/24");
        await InsertInterfaceAsync("hostA", "tailscale0", "100.64.1.2/32");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        SubnetGraphNode vpn = Assert.Single(graph.Nodes, n => n.Kind == "vpn");
        Assert.Equal("Tailscale", vpn.Label);
        SubnetGraphNode tailnet = Assert.Single(graph.Nodes, n => n.Kind == "subnet" && n.Label.Contains("100.64.1.2/32"));
        Assert.Contains(graph.Edges, e => e.FromId == tailnet.Id && e.ToId == vpn.Id);
    }

    [Fact]
    public async Task GetGraph_TailscaleNameOutsideCgnat_NoVpnCloud()
    {
        // A "tailscale"-named interface whose IP is NOT in 100.64.0.0/10 fails the CGNAT
        // confirmation — no cloud is fabricated on a name alone.
        await InsertInterfaceAsync("hostA", "eth0", "192.168.1.10/24");
        await InsertInterfaceAsync("hostA", "tailscale0", "192.168.9.2/24");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == "vpn");
    }

    // ── Track 3: interface labels on edges ──────────────────────────────────────

    [Fact]
    public async Task GetGraph_SpanEdges_CarryInterfaceName()
    {
        await InsertInterfaceAsync("udm", "eth0", "192.168.1.1/24");
        await InsertInterfaceAsync("udm", "eth1", "192.168.10.1/24");

        SubnetGraph graph = await SubnetsApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        List<string?> vias = graph.Edges.Select(e => e.Via).ToList();
        Assert.Contains("eth0", vias);
        Assert.Contains("eth1", vias);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertInterfaceAsync(string device, string interfaceMac, string ipv4, int? ipv4PrefixLength = null)
    {
        const string sql = """
            INSERT INTO proj_interfaces (device, interface, name, ipv4, ipv4_prefix_length)
            VALUES (@device, @iface, @name, @ipv4, @ipv4PrefixLength)
            ON CONFLICT (device, interface) DO UPDATE SET
                ipv4 = EXCLUDED.ipv4, ipv4_prefix_length = EXCLUDED.ipv4_prefix_length
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("iface", interfaceMac);
        cmd.Parameters.AddWithValue("name", interfaceMac);
        cmd.Parameters.AddWithValue("ipv4", ipv4);
        cmd.Parameters.AddWithValue("ipv4PrefixLength", (object?)ipv4PrefixLength ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDhcpScopeAsync(
        string service,
        string scope,
        string startAddress,
        string endAddress,
        string subnetMask,
        string gateway,
        bool enabled = true
    )
    {
        const string sql = """
            INSERT INTO proj_dhcp_scopes (service, scope, enabled, start_address, end_address, subnet_mask, gateway)
            VALUES (@service, @scope, @enabled, @start, @end, @mask, @gateway)
            ON CONFLICT (service, scope) DO UPDATE SET enabled = EXCLUDED.enabled
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("service", service);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("enabled", enabled);
        cmd.Parameters.AddWithValue("start", startAddress);
        cmd.Parameters.AddWithValue("end", endAddress);
        cmd.Parameters.AddWithValue("mask", subnetMask);
        cmd.Parameters.AddWithValue("gateway", gateway);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertArpAsync(string device, string ip, string mac, string iface)
    {
        const string sql = """
            INSERT INTO proj_device_arp (device, arp, mac, iface, state)
            VALUES (@device, @ip, @mac, @iface, @state)
            ON CONFLICT (device, arp) DO UPDATE SET mac = EXCLUDED.mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", mac);
        cmd.Parameters.AddWithValue("iface", iface);
        cmd.Parameters.AddWithValue("state", "reachable");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertRouteAsync(string device, string destination, string? gateway = null, string? iface = null)
    {
        const string sql = """
            INSERT INTO proj_device_routes (device, route, family, gateway, iface)
            VALUES (@device, @route, 'inet', @gateway, @iface)
            ON CONFLICT (device, route) DO UPDATE SET gateway = EXCLUDED.gateway, iface = EXCLUDED.iface
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("route", destination);
        cmd.Parameters.AddWithValue("gateway", (object?)gateway ?? DBNull.Value);
        cmd.Parameters.AddWithValue("iface", (object?)iface ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDockerNetworkAsync(string device, string cidr, string driver, string name = "bridge")
    {
        const string sql = """
            INSERT INTO proj_docker_networks (device, dockernet, name, driver, scope)
            VALUES (@device, @cidr, @name, @driver, 'local')
            ON CONFLICT (device, dockernet) DO UPDATE SET driver = EXCLUDED.driver
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("cidr", cidr);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("driver", driver);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// L2TopologyApi
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class L2TopologyApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public L2TopologyApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "proj_discovered",
            "proj_systems",
            "device_fingerprints",
            "devices"
        );

    [Fact]
    public async Task GetGraph_MeshRelayedClient_UnknownMeshPointBecomesSyntheticNode()
    {
        // Client is independently known (e.g. ARP-discovered elsewhere and fingerprinted); the
        // mesh point relaying it is only ever seen as a BSSID — no direct info about that device.
        Guid clientId = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(clientId, "mac", "aa:bb:cc:00:00:01");
        await InsertDiscoveredMeshLinkAsync("onhub-primary", "client-1", "aa:bb:cc:00:00:01", "11:22:33:44:55:66");

        L2Graph graph = await L2TopologyApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        L2GraphNode clientNode = Assert.Single(graph.Nodes, n => n.Kind == "device" && n.Id == clientId.ToString());
        L2GraphNode meshNode = Assert.Single(graph.Nodes, n => n.Kind == "mesh");
        Assert.Contains("11:22:33:44:55:66", meshNode.Label);
        Assert.Contains(graph.Edges, e => e.Via == "mesh"
            && ((e.FromId == clientNode.Id && e.ToId == meshNode.Id) || (e.FromId == meshNode.Id && e.ToId == clientNode.Id)));
    }

    [Fact]
    public async Task GetGraph_MeshPointAlsoIndependentlyKnown_ReusesItsDeviceNode()
    {
        // The specific mesh point relaying this client has ALSO been independently fingerprinted
        // (e.g. it exposes itself on the LAN and got ARP-discovered too) — its real device node
        // should be reused instead of a synthetic "mesh" placeholder.
        Guid clientId = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(clientId, "mac", "aa:bb:cc:00:00:01");
        Guid meshPointId = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(meshPointId, "mac", "11:22:33:44:55:66");
        await InsertDiscoveredMeshLinkAsync("onhub-primary", "client-1", "aa:bb:cc:00:00:01", "11:22:33:44:55:66");

        L2Graph graph = await L2TopologyApi.GetGraphAsync(_fixture.DataSource, CancellationToken.None);

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == "mesh");
        Assert.Contains(graph.Nodes, n => n.Kind == "device" && n.Id == meshPointId.ToString());
        Assert.Contains(graph.Edges, e => e.Via == "mesh"
            && ((e.FromId == clientId.ToString() && e.ToId == meshPointId.ToString())
             || (e.FromId == meshPointId.ToString() && e.ToId == clientId.ToString())));
    }

    private async Task InsertDiscoveredMeshLinkAsync(string device, string discovered, string mac, string meshApBssid)
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, mac, mesh_ap_bssid)
            VALUES (@device, @discovered, @mac, @bssid)
            ON CONFLICT (device, discovered) DO UPDATE SET mesh_ap_bssid = EXCLUDED.mesh_ap_bssid
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("discovered", discovered);
        cmd.Parameters.AddWithValue("mac", mac);
        cmd.Parameters.AddWithValue("bssid", meshApBssid);
        await cmd.ExecuteNonQueryAsync();
    }
}

[Collection("Integration")]
public sealed class AgentsApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public AgentsApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("agents");

    [Fact]
    public async Task Query_NoAgents_ReturnsEmptyList()
    {
        (List<AgentListItem> items, string? next) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_AgentEntry_ReturnsCorrectFields()
    {
        await InsertAgentAsync("agent-1", "host-a", status: "approved", zone: "home");

        (List<AgentListItem> items, string? next) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Null(next);

        AgentListItem a = items[0];
        Assert.Equal("host-a", a.Hostname);
        Assert.Equal("approved", a.Status);
        Assert.Equal("home", a.Zone);
    }

    [Fact]
    public async Task Query_FilterByStatus_ReturnsOnlyMatchingAgents()
    {
        await InsertAgentAsync("agent-1", "host-a", status: "approved");
        await InsertAgentAsync("agent-2", "host-b", status: "pending");

        (List<AgentListItem> items, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            "pending",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("host-b", items[0].Hostname);
    }

    [Fact]
    public async Task Query_NoSortSpecified_DefaultsToNewestFirst()
    {
        await InsertAgentAsync("agent-1", "host-a", createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await InsertAgentAsync("agent-2", "host-b", createdAt: DateTimeOffset.UtcNow);

        (List<AgentListItem> items, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Equal(2, items.Count);
        Assert.Equal("host-b", items[0].Hostname);
        Assert.Equal("host-a", items[1].Hostname);
    }

    [Fact]
    public async Task Query_SortByStatusAscending_OrdersAndPaginatesByStatus()
    {
        await InsertAgentAsync("agent-1", "host-a", status: "pending");
        await InsertAgentAsync("agent-2", "host-b", status: "approved");
        await InsertAgentAsync("agent-3", "host-c", status: "disabled");

        (List<AgentListItem> page1, string? cursor1) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            2,
            CancellationToken.None,
            sort: "status",
            dir: "asc"
        );

        Assert.Equal(2, page1.Count);
        Assert.Equal("approved", page1[0].Status);
        Assert.Equal("disabled", page1[1].Status);
        Assert.NotNull(cursor1);

        Assert.True(KeysetCursor.TryDecodeParts(cursor1, 3, out string[] parts));

        (List<AgentListItem> page2, string? cursor2) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            parts[0],
            parts[1],
            parts[2],
            2,
            CancellationToken.None,
            sort: "status",
            dir: "asc"
        );

        Assert.Single(page2);
        Assert.Null(cursor2);
        Assert.Equal("pending", page2[0].Status);
    }

    [Fact]
    public async Task Query_SearchByHostnameOrIp_ReturnsOnlyMatchingAgents()
    {
        await InsertAgentAsync("agent-1", "dns-server-01", ipAddress: "10.0.0.5");
        await InsertAgentAsync("agent-2", "other-host", ipAddress: "10.0.0.9");

        (List<AgentListItem> byHostname, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            "dns-server",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byHostname);
        Assert.Equal("dns-server-01", byHostname[0].Hostname);

        (List<AgentListItem> byIp, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            "10.0.0.9",
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byIp);
        Assert.Equal("other-host", byIp[0].Hostname);
    }

    [Fact]
    public async Task Query_FilterByLiveness_ReturnsOnlyMatchingAgents()
    {
        await InsertAgentAsync("agent-1", "never-checked-in", lastHeartbeat: null);
        await InsertAgentAsync("agent-2", "just-beat", lastHeartbeat: DateTimeOffset.UtcNow);

        (List<AgentListItem> offline, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            "offline",
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(offline);
        Assert.Equal("never-checked-in", offline[0].Hostname);

        (List<AgentListItem> online, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            "online",
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(online);
        Assert.Equal("just-beat", online[0].Hostname);
    }

    [Fact]
    public async Task Query_FilterByZoneAndVersion_ReturnsOnlyMatchingAgents()
    {
        await InsertAgentAsync("agent-1", "host-a", zone: "home", version: "1.0.0");
        await InsertAgentAsync("agent-2", "host-b", zone: "office", version: "1.1.0");

        (List<AgentListItem> byZone, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            "office",
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byZone);
        Assert.Equal("host-b", byZone[0].Hostname);

        (List<AgentListItem> byVersion, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            "1.0.0",
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Single(byVersion);
        Assert.Equal("host-a", byVersion[0].Hostname);
    }

    [Fact]
    public async Task GetFilterFacetsAsync_ReturnsDistinctZonesAndVersions()
    {
        await InsertAgentAsync("agent-1", "host-a", zone: "home", version: "1.0.0");
        await InsertAgentAsync("agent-2", "host-b", zone: "office", version: "1.0.0");

        (List<string> zones, List<string> versions) =
            await AgentsApi.GetFilterFacetsAsync(_fixture.DataSource, CancellationToken.None);

        Assert.Equal(["home", "office"], zones);
        Assert.Equal(["1.0.0"], versions);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisableAgentAsync_And_EnableAgentAsync_RoundTripStatus()
    {
        Guid agentId = await InsertAgentAsync("agent-1", "host-a", status: "approved");

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            List<AgentIdResult> disabled = await conn.DisableAgentAsync(agentId, CancellationToken.None)
                .ToListAsync(CancellationToken.None);
            Assert.Single(disabled);
        }

        (List<AgentListItem> afterDisable, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Equal("disabled", afterDisable.Single().Status);

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            List<AgentIdResult> enabled = await conn.EnableAgentAsync(agentId, CancellationToken.None)
                .ToListAsync(CancellationToken.None);
            Assert.Single(enabled);
        }

        (List<AgentListItem> afterEnable, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Equal("approved", afterEnable.Single().Status);
    }

    [Fact]
    public async Task DisableAgentAsync_UnknownAgent_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<AgentIdResult> result = await conn.DisableAgentAsync(Guid.NewGuid(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task EnableAgentAsync_UnknownAgent_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<AgentIdResult> result = await conn.EnableAgentAsync(Guid.NewGuid(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RequestClearTrackersAsync_SetsTimestamp_SurfacedByGetAgentConfig()
    {
        Guid agentId = await InsertAgentAsync("agent-1", "host-a");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        List<(Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore)>
            before = await conn.GetAgentConfigAsync(agentId, CancellationToken.None).ToListAsync(CancellationToken.None);
        Assert.Null(Assert.Single(before).ClearTrackersRequestedAt);

        List<AgentIdResult> result = await conn.RequestClearTrackersAsync(agentId, CancellationToken.None)
            .ToListAsync(CancellationToken.None);
        Assert.Single(result);

        List<(Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore)>
            after = await conn.GetAgentConfigAsync(agentId, CancellationToken.None).ToListAsync(CancellationToken.None);
        Assert.NotNull(Assert.Single(after).ClearTrackersRequestedAt);
    }

    [Fact]
    public async Task RequestLogsAsync_SetsRequestFields_SurfacedByGetAgentConfig()
    {
        Guid agentId = await InsertAgentAsync("agent-logs", "host-logs");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        (Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore) before =
            Assert.Single(await conn.GetAgentConfigAsync(agentId, CancellationToken.None).ToListAsync(CancellationToken.None));
        Assert.Null(before.LogsRequestedAt);
        Assert.Null(before.LogsRequestedLines);
        Assert.Null(before.LogsRequestedBefore);

        (Guid AgentId, DateTimeOffset? LogsRequestedAt) result =
            await conn.RequestLogsAsync(agentId, 500, "cursor-token", CancellationToken.None)
                .FirstOrDefaultAsync(CancellationToken.None);
        Assert.Equal(agentId, result.AgentId);
        Assert.NotNull(result.LogsRequestedAt);

        (Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore) after =
            Assert.Single(await conn.GetAgentConfigAsync(agentId, CancellationToken.None).ToListAsync(CancellationToken.None));
        Assert.NotNull(after.LogsRequestedAt);
        Assert.Equal(500, after.LogsRequestedLines);
        Assert.Equal("cursor-token", after.LogsRequestedBefore);
    }

    [Fact]
    public async Task RequestLogsAsync_UnknownAgent_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(Guid AgentId, DateTimeOffset? LogsRequestedAt)> result =
            await conn.RequestLogsAsync(Guid.NewGuid(), 200, null, CancellationToken.None)
                .ToListAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RequestClearTrackersAsync_UnknownAgent_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<AgentIdResult> result = await conn.RequestClearTrackersAsync(Guid.NewGuid(), CancellationToken.None)
            .ToListAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task UpdateAgentZoneAsync_SetsAndClearsZone()
    {
        Guid agentId = await InsertAgentAsync("agent-1", "host-a", zone: "old-zone");

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            List<AgentIdResult> result = await conn.UpdateAgentZoneAsync(agentId, "new-zone", CancellationToken.None)
                .ToListAsync(CancellationToken.None);
            Assert.Single(result);
        }

        (List<AgentListItem> afterSet, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Equal("new-zone", afterSet.Single().Zone);

        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            List<AgentIdResult> result = await conn.UpdateAgentZoneAsync(agentId, null, CancellationToken.None)
                .ToListAsync(CancellationToken.None);
            Assert.Single(result);
        }

        (List<AgentListItem> afterClear, _) = await AgentsApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );
        Assert.Null(afterClear.Single().Zone);
    }

    [Fact]
    public async Task UpdateAgentZoneAsync_UnknownAgent_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<AgentIdResult> result = await conn.UpdateAgentZoneAsync(Guid.NewGuid(), "zone", CancellationToken.None)
            .ToListAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    private async Task<Guid> InsertAgentAsync(
        string agentIdSeed,
        string hostname,
        string status = "pending",
        string? zone = null,
        string? version = null,
        string? ipAddress = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastHeartbeat = null
    )
    {
        const string sql = """
            INSERT INTO agents (agent_id, hostname, status, api_key_hash, zone, version, ip_address, created_at, last_heartbeat)
            VALUES (@agentId, @hostname, @status, @apiKeyHash, @zone, @version, @ipAddress, @createdAt, @lastHeartbeat)
            """;
        Guid agentId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("hostname", hostname);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("apiKeyHash", "hash-" + agentIdSeed);
        cmd.Parameters.AddWithValue("zone", (object?)zone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ipAddress", (object?)ipAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("createdAt", createdAt ?? DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("lastHeartbeat", (object?)lastHeartbeat ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }
}

[Collection("Integration")]
public sealed class ServicesApiTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ServicesApiTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("proj_services", "proj_systems");

    [Fact]
    public async Task Query_NoServices_ReturnsEmptyList()
    {
        (List<ServiceListItem> items, string? next) = await ServicesApi.QueryAsync(
            _fixture.DataSource,
            null,
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Empty(items);
        Assert.Null(next);
    }

    [Fact]
    public async Task Query_FilterByType_ReturnsOnlyMatchingServices()
    {
        await InsertServiceAsync("dns-1", type: "technitium-dns");
        await InsertServiceAsync("ha-1", type: "home-assistant");

        (List<ServiceListItem> items, _) = await ServicesApi.QueryAsync(
            _fixture.DataSource,
            "home-assistant",
            null,
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("ha-1", items[0].Service);
    }

    [Fact]
    public async Task Query_SearchByHostname_ReturnsOnlyMatchingServices()
    {
        await InsertServiceAsync("dns-1", deviceId: "device-a");
        await InsertServiceAsync("dns-2", deviceId: "device-b");
        await InsertSystemAsync("device-a", "dns-server-01");
        await InsertSystemAsync("device-b", "other-host");

        (List<ServiceListItem> items, _) = await ServicesApi.QueryAsync(
            _fixture.DataSource,
            null,
            "dns-server",
            null,
            10,
            CancellationToken.None
        );

        Assert.Single(items);
        Assert.Equal("dns-1", items[0].Service);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private async Task InsertServiceAsync(string service, string? type = null, string? deviceId = null)
    {
        const string sql = """
            INSERT INTO proj_services (service, type, device_id)
            VALUES (@service, @type, @deviceId)
            ON CONFLICT (service) DO UPDATE SET type = EXCLUDED.type, device_id = EXCLUDED.device_id
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("service", service);
        cmd.Parameters.AddWithValue("type", (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deviceId", (object?)deviceId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSystemAsync(string device, string hostname)
    {
        const string sql = """
            INSERT INTO proj_systems (device, hostname)
            VALUES (@device, @hostname)
            ON CONFLICT (device) DO UPDATE SET hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("hostname", hostname);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServiceQueries.ListServiceHealthAsync — service_down sweep's data source
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ListServiceHealthTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ListServiceHealthTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("proj_service_ca", "proj_services");

    [Fact]
    public async Task CaService_ReportsCaStatus_RegardlessOfUpdatedAt()
    {
        await InsertServiceAsync("ca-1", type: "step-ca", updatedAt: DateTimeOffset.UtcNow.AddDays(-30));
        await InsertServiceCaAsync("ca-1", "running");

        (string Service, string? Type, string? CaStatus, DateTimeOffset UpdatedAt) row = await Query("ca-1");

        Assert.Equal("running", row.CaStatus);
    }

    [Fact]
    public async Task NonCaService_RecentUpdate_HasNoCaStatus()
    {
        await InsertServiceAsync("dns-1", type: "technitium-dns", updatedAt: DateTimeOffset.UtcNow);

        (string Service, string? Type, string? CaStatus, DateTimeOffset UpdatedAt) row = await Query("dns-1");

        Assert.Null(row.CaStatus);
        Assert.True(row.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task NonCaService_StaleUpdate_SurfacesOldTimestamp()
    {
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-2);
        await InsertServiceAsync("ha-1", type: "home-assistant", updatedAt: old);

        (string Service, string? Type, string? CaStatus, DateTimeOffset UpdatedAt) row = await Query("ha-1");

        Assert.Null(row.CaStatus);
        Assert.True(Math.Abs((old - row.UpdatedAt).TotalSeconds) < 1);
    }

    private async Task<(string Service, string? Type, string? CaStatus, DateTimeOffset UpdatedAt)> Query(string service)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await foreach ((string s, string? type, string? caStatus, DateTimeOffset updatedAt) in
            conn.ListServiceHealthAsync(CancellationToken.None))
        {
            if (s == service)
            {
                return (s, type, caStatus, updatedAt);
            }
        }

        throw new InvalidOperationException($"Service '{service}' not found in ListServiceHealthAsync results.");
    }

    private async Task InsertServiceAsync(string service, string? type, DateTimeOffset updatedAt)
    {
        const string sql = """
            INSERT INTO proj_services (service, type, updated_at)
            VALUES (@service, @type, @updatedAt)
            ON CONFLICT (service) DO UPDATE SET type = EXCLUDED.type, updated_at = EXCLUDED.updated_at
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("service", service);
        cmd.Parameters.AddWithValue("type", (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("updatedAt", updatedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertServiceCaAsync(string service, string caStatus)
    {
        const string sql = """
            INSERT INTO proj_service_ca (service, ca_status)
            VALUES (@service, @caStatus)
            ON CONFLICT (service) DO UPDATE SET ca_status = EXCLUDED.ca_status
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("service", service);
        cmd.Parameters.AddWithValue("caStatus", caStatus);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TargetQueries — discovery-assisted candidates (E2)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class TargetCandidatesTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public TargetCandidatesTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "targets",
            "proj_discovered_services",
            "proj_discovered",
            "materialization_facts",
            "agents"
        );

    [Fact]
    public async Task ListTargetCandidatesAsync_FindsSshSnmpCertAndGoogleWifiSignals()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredAsync("10.0.0.1", sources: "SshBannerScanner,ArpScanner");
        await InsertDiscoveredAsync("10.0.0.2", sources: "SnmpBroadcastScanner");
        await InsertDiscoveredAsync("10.0.0.3", sources: "TlsCertScanner");
        await InsertDiscoveredAsync("10.0.0.4", sources: "SsdpScanner", vendor: "Google");
        await InsertDiscoveredAsync("10.0.0.5", sources: "ArpScanner"); // no matching signal

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        Assert.Contains(items, i => i.Endpoint == "10.0.0.1" && i.CollectorType == "ssh");
        Assert.Contains(items, i => i.Endpoint == "10.0.0.2" && i.CollectorType == "snmp");
        Assert.Contains(items, i => i.Endpoint == "10.0.0.3" && i.CollectorType == "cert");
        Assert.Contains(items, i => i.Endpoint == "10.0.0.4" && i.CollectorType == "google-wifi");
        Assert.DoesNotContain(items, i => i.Endpoint == "10.0.0.5");
    }

    [Fact]
    public async Task ListTargetCandidatesAsync_ExcludesAlreadyConfiguredTargets()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredAsync("10.0.0.1", sources: "SshBannerScanner");
        await InsertTargetAsync(agentId, "10.0.0.1", "ssh");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task ListTargetCandidatesAsync_SourceNameIsNotASubstringMatch()
    {
        // A scanner whose name happens to CONTAIN another scanner's name as a substring must
        // not false-positive that other collector type — regression guard for the LIKE '%X%' →
        // exact token (string_to_array + &&) rewrite.
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredAsync("10.0.0.6", sources: "SnmpBroadcastScannerV2");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        Assert.DoesNotContain(items, i => i.Endpoint == "10.0.0.6" && i.CollectorType == "snmp");
    }

    [Fact]
    public async Task ListTargetCandidatesAsync_SshHostKeyAloneStillSignalsSsh()
    {
        // ssh's signal is an OR of two independent conditions (sources token match, or a
        // captured host key) — regression guard that the host-key branch still fires once both
        // conditions live inside one VALUES tuple instead of two separate WHERE clauses.
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredWithHostKeyAsync("10.0.0.7");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        Assert.Contains(items, i => i.Endpoint == "10.0.0.7" && i.CollectorType == "ssh");
    }

    private async Task InsertDiscoveredWithHostKeyAsync(string discovered)
    {
        // ssh_host_key moved to materialization_facts (Phase 3, docs/plans/
        // architecture-identity-facts.md) — the proj_discovered row just needs to exist so
        // ListTargetCandidates' ssh EXISTS-subquery has a row to match, and the host-key signal
        // itself lives in materialization_facts.
        const string sql = """
            INSERT INTO proj_discovered (device, discovered)
            VALUES (@device, @discovered)
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", "observer-device");
        cmd.Parameters.AddWithValue("discovered", discovered);
        await cmd.ExecuteNonQueryAsync();

        const string identitySql = """
            INSERT INTO materialization_facts (device, entity_key, attribute_path, value)
            VALUES ('observer-device', @discovered, 'Device[].Discovered[].SshHostKey', @hostKey)
            """;
        await using NpgsqlCommand identityCmd = new(identitySql, conn);
        identityCmd.Parameters.AddWithValue("discovered", discovered);
        identityCmd.Parameters.AddWithValue("hostKey", "ssh-ed25519 AAAAtest");
        await identityCmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ListTargetCandidatesAsync_FindsHomeAssistantMdnsSignal()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredAsync("10.0.0.9", hostname: "ha.local");
        await InsertDiscoveredServiceAsync("10.0.0.9", "_home-assistant._tcp");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        (string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model) item =
            Assert.Single(items, i => i.CollectorType == "home-assistant");
        Assert.Equal("http://10.0.0.9:8123", item.Endpoint);
    }

    [Fact]
    public async Task ListTargetCandidatesAsync_ExcludesAlreadyConfiguredHomeAssistantTarget()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertDiscoveredAsync("10.0.0.9");
        await InsertDiscoveredServiceAsync("10.0.0.9", "_home-assistant._tcp");
        await InsertTargetAsync(agentId, "http://10.0.0.9:8123", "home-assistant");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)> items =
            await conn.ListTargetCandidatesAsync(agentId, CancellationToken.None).ToListAsync();

        Assert.DoesNotContain(items, i => i.CollectorType == "home-assistant");
    }

    private async Task<Guid> InsertAgentAsync()
    {
        const string sql = """
            INSERT INTO agents (agent_id, hostname, status, api_key_hash)
            VALUES (@agentId, @hostname, 'approved', @apiKeyHash)
            """;
        Guid agentId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("hostname", "candidate-host-" + agentId);
        cmd.Parameters.AddWithValue("apiKeyHash", "hash-" + agentId);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }

    private async Task InsertDiscoveredAsync(
        string discovered,
        string? sources = null,
        string? vendor = null,
        string? hostname = null
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, sources, vendor, hostname)
            VALUES (@device, @discovered, @sources, @vendor, @hostname)
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", "observer-device");
        cmd.Parameters.AddWithValue("discovered", discovered);
        cmd.Parameters.AddWithValue("sources", (object?)sources ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveredServiceAsync(string discovered, string service)
    {
        const string sql = """
            INSERT INTO proj_discovered_services (device, discovered, service)
            VALUES (@device, @discovered, @service)
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", "observer-device");
        cmd.Parameters.AddWithValue("discovered", discovered);
        cmd.Parameters.AddWithValue("service", service);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertTargetAsync(Guid agentId, string endpoint, string collectorType)
    {
        const string sql = """
            INSERT INTO targets (agent_id, endpoint, collector_type)
            VALUES (@agentId, @endpoint, @collectorType)
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("endpoint", endpoint);
        cmd.Parameters.AddWithValue("collectorType", collectorType);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentQueries.ListAgentCyclesAsync — Activity tab date-range/errors-only filter (E5)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ListAgentCyclesTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement;

    public ListAgentCyclesTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("agent_cycles", "agents");

    [Fact]
    public async Task ListAgentCyclesAsync_FiltersByDateRange()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-05T00:00:00Z"));
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-10T00:00:00Z"));

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> rows = await conn
            .ListAgentCyclesAsync(
                agentId,
                DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
                DateTimeOffset.Parse("2026-01-07T00:00:00Z"),
                errorsOnly: false,
                limit: 100,
                collectionOnly: false,
                CancellationToken.None
            )
            .ToListAsync();

        (long CycleId, DateTimeOffset CycleAt, int, int, int, JsonElement, JsonElement, JsonElement, JsonElement)
            row = Assert.Single(rows);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), row.CycleAt);
    }

    [Fact]
    public async Task ListAgentCyclesAsync_ErrorsOnly_ReturnsOnlyCyclesWithErrors()
    {
        Guid agentId = await InsertAgentAsync();
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), errorCount: 0);
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-02T00:00:00Z"), errorCount: 3);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> rows = await conn
            .ListAgentCyclesAsync(agentId, null, null, errorsOnly: true, limit: 100, collectionOnly: false, CancellationToken.None)
            .ToListAsync();

        (long CycleId, DateTimeOffset, int, int, int ErrorCount, JsonElement, JsonElement, JsonElement, JsonElement)
            row = Assert.Single(rows);
        Assert.Equal(3, row.ErrorCount);
    }

    [Fact]
    public async Task ListAgentCyclesAsync_CollectionOnly_ExcludesBareHeartbeatTicks()
    {
        Guid agentId = await InsertAgentAsync();
        JsonElement collectorsRan = JsonDocument.Parse("""[{"name":"os","facts":3,"duration_ms":10}]""").RootElement;
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-01T00:00:00Z")); // heartbeat-only: all arrays empty
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-02T00:00:00Z"), collectors: collectorsRan);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> collectionOnlyRows =
            await conn
                .ListAgentCyclesAsync(agentId, null, null, errorsOnly: false, limit: 100, collectionOnly: true, CancellationToken.None)
                .ToListAsync();
        (long CycleId, DateTimeOffset CycleAt, int, int, int, JsonElement, JsonElement, JsonElement, JsonElement)
            onlyRow = Assert.Single(collectionOnlyRows);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), onlyRow.CycleAt);

        List<(long CycleId, DateTimeOffset CycleAt, int DurationMs, int FactsSent, int ErrorCount, JsonElement
            Collectors, JsonElement Scanners, JsonElement DeviceScanners, JsonElement Services)> allRows = await conn
            .ListAgentCyclesAsync(agentId, null, null, errorsOnly: false, limit: 100, collectionOnly: false, CancellationToken.None)
            .ToListAsync();
        Assert.Equal(2, allRows.Count);
    }

    private async Task<Guid> InsertAgentAsync()
    {
        const string sql = """
            INSERT INTO agents (agent_id, hostname, status, api_key_hash)
            VALUES (@agentId, @hostname, 'approved', @apiKeyHash)
            """;
        Guid agentId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("hostname", "cycle-host-" + agentId);
        cmd.Parameters.AddWithValue("apiKeyHash", "hash-" + agentId);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }

    private async Task InsertCycleAsync(
        Guid agentId,
        DateTimeOffset cycleAt,
        int errorCount = 0,
        JsonElement? collectors = null
    )
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.InsertAgentCycleAsync(
                agentId,
                cycleAt,
                durationMs: 100,
                factsSent: 10,
                errorCount,
                collectors ?? EmptyArray,
                EmptyArray,
                EmptyArray,
                EmptyArray,
                CancellationToken.None
            )
            .ToListAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentQueries.GetCollectorHealthSummaryAsync — per-collector health strip (E6)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class CollectorHealthSummaryTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public CollectorHealthSummaryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("agent_cycles", "agents");

    [Fact]
    public async Task GetCollectorHealthSummaryAsync_AggregatesRunsErrorsAndMedianDuration()
    {
        Guid agentId = await InsertAgentAsync();
        JsonElement okCycle = JsonDocument.Parse(
            """[{"name":"OsCollector","duration_ms":100},{"name":"DockerCollector","duration_ms":200}]"""
        ).RootElement;
        JsonElement erroredCycle = JsonDocument.Parse(
            """[{"name":"OsCollector","duration_ms":300},{"name":"DockerCollector","duration_ms":50,"error":"timeout"}]"""
        ).RootElement;

        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), collectors: okCycle);
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-02T00:00:00Z"), collectors: erroredCycle);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs)> rows =
            await conn.GetCollectorHealthSummaryAsync(agentId, null, null, CancellationToken.None).ToListAsync();

        (string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) docker =
            rows.Single(r => r.Name == "DockerCollector");
        Assert.Equal("collector", docker.Kind);
        Assert.Equal(2, docker.RunCount);
        Assert.Equal(1, docker.ErrorCount);

        (string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) os =
            rows.Single(r => r.Name == "OsCollector");
        Assert.Equal(2, os.RunCount);
        Assert.Equal(0, os.ErrorCount);
        Assert.Equal(200, os.MedianDurationMs);
    }

    [Fact]
    public async Task GetCollectorHealthSummaryAsync_FiltersByDateRange()
    {
        Guid agentId = await InsertAgentAsync();
        JsonElement cycle = JsonDocument.Parse("""[{"name":"OsCollector","duration_ms":100}]""").RootElement;

        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), collectors: cycle);
        await InsertCycleAsync(agentId, DateTimeOffset.Parse("2026-02-01T00:00:00Z"), collectors: cycle);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs)> rows = await conn
            .GetCollectorHealthSummaryAsync(
                agentId,
                DateTimeOffset.Parse("2026-01-15T00:00:00Z"),
                null,
                CancellationToken.None
            )
            .ToListAsync();

        (string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) row =
            Assert.Single(rows);
        Assert.Equal(1, row.RunCount);
    }

    [Fact]
    public async Task GetCollectorHealthSummaryAsync_SameNameDifferentKind_ReportedAsSeparateRows()
    {
        // ArpCollector (a local collector) and ArpScanner (a network scanner) both report the
        // runtime name "arp" — Kind must keep their stats from being merged into one row.
        Guid agentId = await InsertAgentAsync();
        JsonElement arpCollector = JsonDocument.Parse("""[{"name":"arp","duration_ms":10}]""").RootElement;
        JsonElement arpScanner = JsonDocument.Parse("""[{"name":"arp","duration_ms":20,"error":"timeout"}]""")
            .RootElement;
        JsonElement emptyArray = JsonDocument.Parse("[]").RootElement;

        await using NpgsqlConnection insertConn = await _fixture.DataSource.OpenConnectionAsync();
        await insertConn.InsertAgentCycleAsync(
                agentId,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                durationMs: 100,
                factsSent: 10,
                errorCount: 1,
                arpCollector,
                arpScanner,
                emptyArray,
                emptyArray,
                CancellationToken.None
            )
            .ToListAsync();

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs)> rows =
            await conn.GetCollectorHealthSummaryAsync(agentId, null, null, CancellationToken.None).ToListAsync();

        Assert.Equal(2, rows.Count(r => r.Name == "arp"));
        (string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) collectorRow =
            rows.Single(r => r.Name == "arp" && r.Kind == "collector");
        Assert.Equal(0, collectorRow.ErrorCount);
        (string? Name, string? Kind, int? RunCount, int? ErrorCount, double? MedianDurationMs) scannerRow =
            rows.Single(r => r.Name == "arp" && r.Kind == "scanner");
        Assert.Equal(1, scannerRow.ErrorCount);
    }

    private async Task<Guid> InsertAgentAsync()
    {
        const string sql = """
            INSERT INTO agents (agent_id, hostname, status, api_key_hash)
            VALUES (@agentId, @hostname, 'approved', @apiKeyHash)
            """;
        Guid agentId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("hostname", "health-host-" + agentId);
        cmd.Parameters.AddWithValue("apiKeyHash", "hash-" + agentId);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }

    private async Task InsertCycleAsync(Guid agentId, DateTimeOffset cycleAt, JsonElement collectors)
    {
        JsonElement emptyArray = JsonDocument.Parse("[]").RootElement;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.InsertAgentCycleAsync(
                agentId,
                cycleAt,
                durationMs: 100,
                factsSent: 10,
                errorCount: 0,
                collectors,
                emptyArray,
                emptyArray,
                emptyArray,
                CancellationToken.None
            )
            .ToListAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// agent_liveness() SQL function + agent_liveness_settings (E8)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class AgentLivenessSettingsTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public AgentLivenessSettingsTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // agent_liveness_settings is a single-row table (never truncated) — reset to defaults
        // so other tests relying on the default 3x/3600s thresholds aren't affected.
        await _fixture.TruncateAsync("agents");
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.UpdateAgentLivenessSettingsAsync(3, 3600, CancellationToken.None).ToListAsync();
    }

    [Fact]
    public async Task GetAgentLivenessSettingsAsync_ReturnsDefaults()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        (int OnlineMultiplier, int OfflineCeilingSecs) settings =
            await conn.GetAgentLivenessSettingsAsync(CancellationToken.None).FirstAsync();

        Assert.Equal(3, settings.OnlineMultiplier);
        Assert.Equal(3600, settings.OfflineCeilingSecs);
    }

    [Fact]
    public async Task UpdateAgentLivenessSettingsAsync_ChangesThresholdsUsedByAgentLiveness()
    {
        Guid agentId = await InsertAgentAsync(lastHeartbeatSecsAgo: 200, heartbeatIntervalSecs: 30);

        // 200s ago is stale under the default 3x30=90s online threshold.
        string? liveness1 = await GetLivenessAsync(agentId);
        Assert.Equal("stale", liveness1);

        // Widen the online multiplier so 200s now falls within it (10 * 30 = 300s).
        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            await conn.UpdateAgentLivenessSettingsAsync(10, 3600, CancellationToken.None).ToListAsync();
        }

        string? liveness2 = await GetLivenessAsync(agentId);
        Assert.Equal("online", liveness2);
    }

    private async Task<string?> GetLivenessAsync(Guid agentId)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone, string?
            Version, string? Os, string? Arch, string? IpAddress, Guid? DeviceId, int HeartbeatIntervalSecs, int
            DiscoveryIntervalSecs, int InventoryIntervalSecs, JsonElement CollectorsConfig, string? Liveness,
            JsonElement? Capabilities)> rows =
            await conn.GetAgentDetailAsync(agentId, CancellationToken.None).ToListAsync();
        return rows.Single().Liveness;
    }

    private async Task<Guid> InsertAgentAsync(int lastHeartbeatSecsAgo, int heartbeatIntervalSecs)
    {
        const string sql = """
            INSERT INTO agents (agent_id, hostname, status, api_key_hash, last_heartbeat, heartbeat_interval_secs)
            VALUES (@agentId, @hostname, 'approved', @apiKeyHash, now() - make_interval(secs => @secsAgo), @intervalSecs)
            """;
        Guid agentId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("agentId", agentId);
        cmd.Parameters.AddWithValue("hostname", "liveness-host-" + agentId);
        cmd.Parameters.AddWithValue("apiKeyHash", "hash-" + agentId);
        cmd.Parameters.AddWithValue("secsAgo", lastHeartbeatSecsAgo);
        cmd.Parameters.AddWithValue("intervalSecs", heartbeatIntervalSecs);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DeviceQueries.GetDeviceFactsBySourceAsync — collector blast radius (F4)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class GetDeviceFactsBySourceTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public GetDeviceFactsBySourceTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("facts_history", "devices");

    [Fact]
    public async Task GetDeviceFactsBySourceAsync_ReturnsOnlyLatestFactsFromThatCollector()
    {
        Guid deviceId = await InsertDeviceAsync();

        // Docker fact: two writes, latest wins and is attributed to Docker.
        await InsertFactAsync(
            id: "Device[" + deviceId + "].Docker.ContainerCount",
            deviceId,
            "3",
            "Docker",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        );
        await InsertFactAsync(
            id: "Device[" + deviceId + "].Docker.ContainerCount",
            deviceId,
            "5",
            "Docker",
            DateTimeOffset.Parse("2026-01-02T00:00:00Z")
        );

        // Os fact: a different collector entirely — must not appear in Docker's results.
        await InsertFactAsync(
            id: "Device[" + deviceId + "].Os.Hostname",
            deviceId,
            "myhost",
            "Os",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        );

        // A fact that WAS Docker's but was later overwritten by a different collector —
        // must not appear, since Docker no longer owns the current value.
        await InsertFactAsync(
            id: "Device[" + deviceId + "].Docker.Version",
            deviceId,
            "old",
            "Docker",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        );
        await InsertFactAsync(
            id: "Device[" + deviceId + "].Docker.Version",
            deviceId,
            "new",
            "ArpLocal",
            DateTimeOffset.Parse("2026-01-02T00:00:00Z")
        );

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<(string AttributePath, string? KeyValues, string? Value, DateTimeOffset CollectedAt)> rows =
            await conn.GetDeviceFactsBySourceAsync(deviceId, "Docker", CancellationToken.None).ToListAsync();

        (string AttributePath, string?, string? Value, DateTimeOffset) row = Assert.Single(rows);
        Assert.EndsWith("Docker.ContainerCount", row.AttributePath);
        Assert.Equal("5", row.Value);
    }

    private async Task<Guid> InsertDeviceAsync()
    {
        const string sql = "INSERT INTO devices (device_id) VALUES (@deviceId)";
        Guid deviceId = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        await cmd.ExecuteNonQueryAsync();
        return deviceId;
    }

    private async Task InsertFactAsync(
        string id,
        Guid deviceId,
        string value,
        string sourceName,
        DateTimeOffset collectedAt
    )
    {
        const string sql = """
            INSERT INTO facts_history (id, attribute_path, key_values, kind, value_str, collected_at, source_name)
            VALUES (@id, @attributePath, @keyValues::jsonb, 0, @value, @collectedAt, @sourceName)
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("attributePath", id.Contains("Docker.ContainerCount")
            ? "Device[].Docker.ContainerCount"
            : id.Contains("Docker.Version")
                ? "Device[].Docker.Version"
                : "Device[].Os.Hostname");
        cmd.Parameters.AddWithValue("keyValues", $$"""{"Device":"{{deviceId}}"}""");
        cmd.Parameters.AddWithValue("value", value);
        cmd.Parameters.AddWithValue("collectedAt", collectedAt);
        cmd.Parameters.AddWithValue("sourceName", sourceName);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServiceQueries.ResolveServiceHostDevice — endpoint-IP → host device linkage
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class ResolveServiceHostDeviceTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ResolveServiceHostDeviceTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await _fixture.TruncateAsync(
            "proj_interfaces",
            "proj_systems",
            "proj_device_arp",
            "device_fingerprints",
            "device_aliases",
            "devices"
        );

    [Fact]
    public async Task Resolves_ByOwnInterfaceIp()
    {
        Guid id = await _fixture.InsertDeviceAsync("managed");
        await ExecuteAsync(
            $"INSERT INTO proj_interfaces (device, interface, name, ipv4) VALUES ('{id}', 'eth0', 'eth0', '192.168.1.60/24')"
        );

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        ServiceHostDevice? r = await conn.ResolveServiceHostDeviceAsync("192.168.1.60", CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(id.ToString(), r?.DeviceId);
    }

    [Fact]
    public async Task Resolves_ByArpNeighborMac()
    {
        // No interface row — the host is known only as an ARP neighbor: IP -> MAC -> device.
        Guid id = await _fixture.InsertDeviceAsync("discovered");
        await _fixture.InsertFingerprintAsync(id, "mac", "aabbccddeeff");
        await ExecuteAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES ('observer-1', '192.168.1.61', 'aabbccddeeff', 'eth0', 'reachable', now())"
        );

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        ServiceHostDevice? r = await conn.ResolveServiceHostDeviceAsync("192.168.1.61", CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(id.ToString(), r?.DeviceId);
    }

    [Fact]
    public async Task Resolves_PrefersOwnInterfaceOverArp()
    {
        // The same IP is both deviceA's own interface address and an ARP neighbor mapping to
        // deviceB — the device whose interface IP it actually is must win (rank order).
        Guid deviceA = await _fixture.InsertDeviceAsync("managed");
        Guid deviceB = await _fixture.InsertDeviceAsync("discovered");
        await ExecuteAsync(
            $"INSERT INTO proj_interfaces (device, interface, name, ipv4) VALUES ('{deviceA}', 'eth0', 'eth0', '192.168.1.62/24')"
        );
        await _fixture.InsertFingerprintAsync(deviceB, "mac", "112233445566");
        await ExecuteAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES ('observer-1', '192.168.1.62', '112233445566', 'eth0', 'reachable', now())"
        );

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        ServiceHostDevice? r = await conn.ResolveServiceHostDeviceAsync("192.168.1.62", CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(deviceA.ToString(), r?.DeviceId);
    }

    [Fact]
    public async Task Excludes_MergedAliasDevice()
    {
        // A device merged away (alias) must never be linked — live_devices filters it out.
        Guid loser = await _fixture.InsertDeviceAsync("discovered");
        Guid survivor = await _fixture.InsertDeviceAsync("managed");
        await ExecuteAsync(
            $"INSERT INTO proj_interfaces (device, interface, name, ipv4) VALUES ('{loser}', 'eth0', 'eth0', '192.168.1.63/24')"
        );
        await _fixture.InsertAliasAsync(loser, survivor);

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        ServiceHostDevice? r = await conn.ResolveServiceHostDeviceAsync("192.168.1.63", CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.Null(r);
    }

    [Fact]
    public async Task NoMatch_ReturnsNoRows()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        ServiceHostDevice? r = await conn.ResolveServiceHostDeviceAsync("10.99.99.99", CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        Assert.Null(r);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PreferencesQueries — per-user preference storage (table column widths, etc.)
// ─────────────────────────────────────────────────────────────────────────────

[Collection("Integration")]
public sealed class UserPreferencesTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public UserPreferencesTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.TruncateAsync("user_preferences", "users");

    [Fact]
    public async Task Upsert_ThenGet_RoundTripsValue()
    {
        Guid userId = await InsertUserAsync("prefs-user-1");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.UpsertUserPreferenceAsync(userId, "cols:devices", Json("""{"0":120,"1":80}"""), CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        UserPreferenceValue row = await conn.GetUserPreferenceAsync(userId, "cols:devices", CancellationToken.None)
            .FirstAsync(CancellationToken.None);

        Assert.Equal(120, row.PrefValue.GetProperty("0").GetInt32());
        Assert.Equal(80, row.PrefValue.GetProperty("1").GetInt32());
    }

    [Fact]
    public async Task Upsert_Overwrites_ExistingValue()
    {
        Guid userId = await InsertUserAsync("prefs-user-2");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.UpsertUserPreferenceAsync(userId, "cols:devices", Json("""{"0":100}"""), CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);
        await conn.UpsertUserPreferenceAsync(userId, "cols:devices", Json("""{"0":200}"""), CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        UserPreferenceValue row = await conn.GetUserPreferenceAsync(userId, "cols:devices", CancellationToken.None)
            .FirstAsync(CancellationToken.None);

        Assert.Equal(200, row.PrefValue.GetProperty("0").GetInt32());
    }

    [Fact]
    public async Task Get_Unset_ReturnsNoRows()
    {
        Guid userId = await InsertUserAsync("prefs-user-3");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        List<UserPreferenceValue> rows = await conn.GetUserPreferenceAsync(userId, "cols:nope", CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Preferences_AreScopedPerUser()
    {
        Guid a = await InsertUserAsync("prefs-user-a");
        Guid b = await InsertUserAsync("prefs-user-b");

        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await conn.UpsertUserPreferenceAsync(a, "cols:devices", Json("""{"0":111}"""), CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        // b must not see what a saved for the same key.
        List<UserPreferenceValue> bRows = await conn.GetUserPreferenceAsync(b, "cols:devices", CancellationToken.None)
            .ToListAsync(CancellationToken.None);

        Assert.Empty(bRows);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<Guid> InsertUserAsync(string username)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "INSERT INTO users (user_id, username, password_hash, role) VALUES (@id, @u, 'x', 'viewer')",
            conn
        );
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("u", username);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }
}