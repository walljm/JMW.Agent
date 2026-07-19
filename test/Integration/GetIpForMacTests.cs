using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Behavioural tests for GetIpForMac — the MAC→current-IP resolver behind mac-keyed collection
/// targets. Schema/column validation is covered automatically by ServerQueryValidationTests; these
/// focus on the resolution logic: source union (ARP + DHCP), IPv4/recency ordering, agent-scoping,
/// and the unseen-MAC (no-row) case that makes a mac-keyed target skip a config cycle rather than
/// ship a broken endpoint.
/// </summary>
[Collection("Integration")]
public sealed class GetIpForMacTests
{
    private readonly IntegrationFixture _fx;

    public GetIpForMacTests(IntegrationFixture fx) => _fx = fx;

    private static readonly string[] Tables =
    [
        "proj_device_arp", "proj_dhcp_leases", "proj_dhcp_local_leases", "proj_discovered",
    ];

    private async Task ResetAsync() => await _fx.TruncateAsync(Tables);

    private async Task ExecAsync(string sql, params (string, object?)[] ps)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, object? val) in ps)
        {
            cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ArpAsync(string device, string ip, string mac, Guid? agentId, DateTimeOffset updatedAt) =>
        await ExecAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at, agent_id) "
          + "VALUES (@d, @ip, @mac, 'eth0', 'reachable', @u, @a)",
            ("d", device),
            ("ip", ip),
            ("mac", mac),
            ("u", updatedAt),
            ("a", (object?)agentId)
        );

    private async Task<string?> ResolveAsync(string mac, Guid? agentId)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        ResolvedIpResult r = await conn.GetIpForMacAsync(mac, agentId, CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);
        return r.Ip;
    }

    [Fact]
    public async Task Resolves_arp_mac_to_ip()
    {
        await ResetAsync();
        Guid agent = Guid.NewGuid();
        await ArpAsync("obs", "192.168.1.42", "703acbeaa759", agent, DateTimeOffset.UtcNow);

        Assert.Equal("192.168.1.42", await ResolveAsync("703acbeaa759", agent));
    }

    [Fact]
    public async Task Resolves_from_dhcp_lease()
    {
        await ResetAsync();
        Guid agent = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO proj_dhcp_leases (service, scope, lease, ip, updated_at, agent_id) "
          + "VALUES ('dhcp-svc', 'scope1', @mac, @ip, now(), @a)",
            ("mac", "703acbeaa759"),
            ("ip", "192.168.1.7"),
            ("a", agent)
        );

        Assert.Equal("192.168.1.7", await ResolveAsync("703acbeaa759", agent));
    }

    [Fact]
    public async Task Prefers_most_recently_seen_ip()
    {
        // The MAC moved by DHCP: an older ARP sighting has it at .10, a fresh one at .80. The
        // resolved address must follow the move so a mac-keyed target hits the current IP.
        await ResetAsync();
        Guid agent = Guid.NewGuid();
        await ArpAsync("obs1", "192.168.1.10", "aabbccddeeff", agent, DateTimeOffset.UtcNow.AddDays(-2));
        await ArpAsync("obs2", "192.168.1.80", "aabbccddeeff", agent, DateTimeOffset.UtcNow);

        Assert.Equal("192.168.1.80", await ResolveAsync("aabbccddeeff", agent));
    }

    [Fact]
    public async Task Prefers_ipv4_over_ipv6_even_when_ipv6_is_fresher()
    {
        await ResetAsync();
        Guid agent = Guid.NewGuid();
        await ArpAsync("obs1", "192.168.1.10", "aabbccddeeff", agent, DateTimeOffset.UtcNow.AddDays(-1));
        await ArpAsync("obs2", "2001:db8::1", "aabbccddeeff", agent, DateTimeOffset.UtcNow);

        Assert.Equal("192.168.1.10", await ResolveAsync("aabbccddeeff", agent));
    }

    [Fact]
    public async Task Excludes_other_agents_but_includes_unscoped_rows()
    {
        await ResetAsync();
        Guid mine = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        // A sighting on a *different* agent's LAN must not resolve (RFC1918 reuse across sites).
        await ArpAsync("obs-other", "192.168.1.99", "112233445566", other, DateTimeOffset.UtcNow);
        Assert.Null(await ResolveAsync("112233445566", mine));

        // A pre-scoping row (null agent_id) is treated as unscoped and is included.
        await ArpAsync("obs-null", "192.168.1.5", "112233445566", null, DateTimeOffset.UtcNow);
        Assert.Equal("192.168.1.5", await ResolveAsync("112233445566", mine));
    }

    [Fact]
    public async Task Unseen_mac_returns_no_row()
    {
        // No sighting anywhere → no IP; the caller (config assembly) skips the target this cycle.
        await ResetAsync();
        Assert.Null(await ResolveAsync("ffffffffffff", Guid.NewGuid()));
    }
}