using JMW.Discovery.Core;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Projections;
using JMW.Discovery.Server.Reporting;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Covers the class of bug that shipped <c>proj_docker_networks</c> empty in migration 0091: facts
/// that landed in <c>facts_history</c> before their projection existed never reach the projection
/// (live routing only, no replay; agents delta-track). Two independent guards are tested here:
/// (1) <see cref="ProjectionBackfill" /> reconstructs those facts from history and populates the
/// projection, and (2) <see cref="SubnetsApi" /> falls back to the route table's interface name to
/// classify host-local Docker bridges even when <c>proj_docker_networks</c> has no data at all.
/// </summary>
[Collection("Integration")]
public sealed class ProjectionBackfillTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public ProjectionBackfillTests(IntegrationFixture fixture) => _fixture = fixture;

    private const string HostA = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string HostB = "bbbbbbbb-2222-2222-2222-222222222222";

    public async Task InitializeAsync() =>
        await _fixture.TruncateAsync(
            "facts_history",
            "proj_docker_networks",
            "proj_device_routes",
            "proj_interfaces",
            "proj_dhcp_scopes",
            "proj_device_arp",
            "proj_dhcp_leases",
            "proj_dhcp_local_leases",
            "proj_discovered",
            "projection_backfills"
        );

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Systemic backfill ───────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_PopulatesEmptyProjectionFromHistory_AndIsIdempotent()
    {
        FactRepository factRepo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));

        // Docker network facts land in history exactly as a live agent would emit them — but WITHOUT
        // routing, simulating the deploy-skew window where the projection did not yet exist.
        await factRepo.AppendAsync(
        [
            .. DockerNetFacts(HostA, "172.17.0.0/16", "bridge", "docker0"),
            .. DockerNetFacts(HostA, "172.18.0.0/16", "pds_default", "br-436a2f158f99"),
            .. DockerNetFacts(HostB, "172.17.0.0/16", "bridge", "docker0"),
            .. DockerNetFacts(HostB, "172.19.0.0/16", "social_default", "br-045e79ec9cae"),
        ]
        );

        // Bug state: facts exist in history, projection is empty.
        Assert.Equal(0, await _fixture.CountAsync("proj_docker_networks"));

        await RunBackfillAsync();

        // Both hosts' identical 172.17.0.0/16 stay distinct rows (keyed per host), plus each host's
        // own user-defined bridge — four rows total, not three merged ones.
        Assert.Equal(4, await _fixture.CountAsync("proj_docker_networks"));
        Assert.Equal(2, await _fixture.CountAsync("proj_docker_networks", "dockernet = '172.17.0.0/16'"));
        Assert.Equal(
            1,
            await _fixture.CountAsync("proj_docker_networks", "device = '" + HostA + "' AND bridge_name = 'docker0'")
        );
        Assert.Equal(1, await _fixture.CountAsync("projection_backfills", "table_name = 'proj_docker_networks'"));

        // Idempotent: a second run neither duplicates rows nor throws.
        await RunBackfillAsync();
        Assert.Equal(4, await _fixture.CountAsync("proj_docker_networks"));
    }

    [Fact]
    public async Task Backfill_SkipsProjectionThatIsAlreadyWatermarked()
    {
        FactRepository factRepo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        await factRepo.AppendAsync([.. DockerNetFacts(HostA, "172.17.0.0/16", "bridge", "docker0")]);

        // Pre-mark the projection as already backfilled; the pass must not touch it.
        await using (NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using NpgsqlCommand cmd = new(
                "INSERT INTO projection_backfills (table_name) VALUES ('proj_docker_networks')",
                conn
            );
            await cmd.ExecuteNonQueryAsync();
        }

        await RunBackfillAsync();

        Assert.Equal(0, await _fixture.CountAsync("proj_docker_networks"));
    }

    // ── Route-table fallback for host-local classification (Docker API unavailable) ──

    [Theory]
    [InlineData("docker0", true)] // default Docker bridge
    [InlineData("docker_gwbridge", true)] // swarm gateway bridge
    [InlineData("br-436a2f158f99", true)] // user-defined Docker bridge (12 hex)
    [InlineData("br-lan", false)] // OpenWrt LAN bridge — routable, not host-local NAT
    [InlineData("br-wan", false)] // OpenWrt WAN bridge
    [InlineData("eth0", false)] // ordinary NIC
    public async Task SubnetsApi_UsesRouteInterface_ToClassifyHostLocal_WhenDockerNetworksEmpty(
        string iface,
        bool expectHostLocal
    )
    {
        // Same CIDR reached through `iface` on two different hosts, with proj_docker_networks empty.
        // A Docker bridge must key per host (two rows, host-local); anything else stays one shared row.
        const string cidr = "172.17.0.0/16";
        await InsertRouteAsync(HostA, cidr, iface);
        await InsertRouteAsync(HostB, cidr, iface);

        List<SubnetListItem> items =
            (await SubnetsApi.QueryAsync(_fixture.DataSource, null, CancellationToken.None))
            .Where(i => i.Cidr == cidr)
            .ToList();

        if (expectHostLocal)
        {
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.True(i.HostLocal));
            Assert.All(items, i => Assert.NotNull(i.Host));
        }
        else
        {
            SubnetListItem shared = Assert.Single(items);
            Assert.False(shared.HostLocal);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task RunBackfillAsync()
    {
        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_fixture.DataSource);
        FactRepository factRepo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        ProjectionRouter router = new(_fixture.DataSource, projections);
        List<ProjectionDef> defs = projections.OfType<GenericProjection>().Select(p => p.Def).ToList();

        await ProjectionBackfill.RunAsync(
            _fixture.DataSource,
            router,
            factRepo,
            defs,
            NullLogger.Instance,
            ct: CancellationToken.None
        );
    }

    private static IEnumerable<Fact> DockerNetFacts(string device, string cidr, string name, string bridge) =>
    [
        Fact.Create($"Device[{device}].DockerNet[{cidr}].Name", name),
        Fact.Create($"Device[{device}].DockerNet[{cidr}].Driver", "bridge"),
        Fact.Create($"Device[{device}].DockerNet[{cidr}].Scope", "local"),
        Fact.Create($"Device[{device}].DockerNet[{cidr}].BridgeName", bridge),
    ];

    private async Task InsertRouteAsync(string device, string route, string iface)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            """
            INSERT INTO proj_device_routes (device, route, family, iface)
            VALUES (@device, @route, 'inet', @iface)
            ON CONFLICT (device, route) DO UPDATE SET iface = EXCLUDED.iface
            """,
            conn
        );
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("route", route);
        cmd.Parameters.AddWithValue("iface", iface);
        await cmd.ExecuteNonQueryAsync();
    }
}