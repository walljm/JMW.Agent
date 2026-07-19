using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Behavioural tests for the co-location scoping of GetKnownMacsForIp — the IP→MAC lookup behind
/// obscured Google Wifi reconstruction. Schema validation is covered by ServerQueryValidationTests;
/// these focus on the agent_colocation relation: a real MAC captured in ARP by one agent is
/// recallable from a lookup keyed on a *co-located* agent (they share &gt;= 3 globally-unique ARP
/// MACs, i.e. one LAN), but never from an agent that is not co-located — which is what keeps
/// same-LAN cross-agent recall from collapsing distinct RFC1918 sites.
/// </summary>
[Collection("Integration")]
public sealed class GetKnownMacsColocationTests
{
    private readonly IntegrationFixture _fx;

    public GetKnownMacsColocationTests(IntegrationFixture fx) => _fx = fx;

    private static readonly string[] Tables =
    [
        "proj_device_arp", "proj_dhcp_leases", "proj_dhcp_local_leases", "proj_discovered",
    ];

    // Globally-administered unicast MACs (first-octet low nibble has the multicast 0x01 and
    // locally-administered 0x02 bits clear): 00, 04, 08 → nibbles 0, 4, 8.
    private static readonly string[] SharedMacs = ["001122334455", "044455667788", "08aabbccddee"];
    private const string TargetMac = "cca7c15bd8f7"; // the real MAC to be recalled at the station IP
    private const string StationIp = "192.168.1.241";

    private async Task ResetAsync() => await _fx.TruncateAsync(Tables);

    private async Task ArpAsync(string ip, string mac, Guid? agentId) =>
        await ExecAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at, agent_id) "
          + "VALUES (@d, @ip, @mac, 'eth0', 'reachable', now(), @a)",
            ("d", Guid.NewGuid().ToString()),
            ("ip", ip),
            ("mac", mac),
            ("a", (object?)agentId)
        );

    /// <summary>Seeds <paramref name="count" /> shared global-unicast MACs into both agents' ARP
    /// so the co-location view links them (needs >= 3 to cross the threshold).</summary>
    private async Task MakeColocatedAsync(Guid a, Guid b, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ArpAsync($"10.0.0.{i + 2}", SharedMacs[i], a);
            await ArpAsync($"10.0.0.{i + 2}", SharedMacs[i], b);
        }
    }

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

    private async Task<List<string>> LookupAsync(string ip, Guid? agentId)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        List<string> macs = [];
        await foreach (DiscoveredMacResult r in conn.GetKnownMacsForIpAsync(ip, agentId, CancellationToken.None))
        {
            if (r.Mac is { } m)
            {
                macs.Add(m);
            }
        }

        return macs;
    }

    [Fact]
    public async Task Recalls_mac_captured_by_a_colocated_agent()
    {
        await ResetAsync();
        Guid poller = Guid.NewGuid(); // e.g. the Google Wifi poller
        Guid arpAgent = Guid.NewGuid(); // e.g. the agent whose ARP captured the real MAC
        await MakeColocatedAsync(poller, arpAgent, 3);

        // Only arpAgent saw the station's real MAC; poller (which only gets obscured MACs) did not.
        await ArpAsync(StationIp, TargetMac, arpAgent);

        Assert.Contains(TargetMac, await LookupAsync(StationIp, poller));
    }

    [Fact]
    public async Task Recall_is_symmetric()
    {
        await ResetAsync();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        await MakeColocatedAsync(a, b, 3);
        await ArpAsync(StationIp, TargetMac, a);

        // b is co-located with a, so a lookup keyed on b recalls a's MAC too.
        Assert.Contains(TargetMac, await LookupAsync(StationIp, b));
    }

    [Fact]
    public async Task Excludes_mac_from_a_non_colocated_agent()
    {
        await ResetAsync();
        Guid poller = Guid.NewGuid();
        Guid stranger = Guid.NewGuid(); // shares no ARP MACs → different site, same RFC1918 IP

        await ArpAsync(StationIp, TargetMac, stranger);

        Assert.DoesNotContain(TargetMac, await LookupAsync(StationIp, poller));
    }

    [Fact]
    public async Task Below_threshold_is_not_colocated()
    {
        await ResetAsync();
        Guid poller = Guid.NewGuid();
        Guid arpAgent = Guid.NewGuid();
        await MakeColocatedAsync(poller, arpAgent, 2); // only 2 shared MACs — under the >=3 threshold
        await ArpAsync(StationIp, TargetMac, arpAgent);

        Assert.DoesNotContain(TargetMac, await LookupAsync(StationIp, poller));
    }

    [Fact]
    public async Task Locally_administered_shared_macs_do_not_prove_colocation()
    {
        await ResetAsync();
        Guid poller = Guid.NewGuid();
        Guid arpAgent = Guid.NewGuid();

        // 3 shared MACs, but all locally-administered (first octet 0x02 bit set: 02, 06, 0a) —
        // randomized/non-unique, so they are not admissible co-location evidence.
        foreach ((string mac, int i) in new[] { ("021122334455", 0), ("064455667788", 1), ("0aaabbccddee", 2) })
        {
            await ArpAsync($"10.0.0.{i + 2}", mac, poller);
            await ArpAsync($"10.0.0.{i + 2}", mac, arpAgent);
        }

        await ArpAsync(StationIp, TargetMac, arpAgent);

        Assert.DoesNotContain(TargetMac, await LookupAsync(StationIp, poller));
    }

    [Fact]
    public async Task Own_agent_and_unscoped_rows_still_recall()
    {
        await ResetAsync();
        Guid poller = Guid.NewGuid();

        // Own-agent capture and a pre-scoping (null agent_id) row are recalled without co-location.
        await ArpAsync(StationIp, TargetMac, poller);
        await ArpAsync(StationIp, "abcdefabcdef", null);

        List<string> macs = await LookupAsync(StationIp, poller);
        Assert.Contains(TargetMac, macs);
        Assert.Contains("abcdefabcdef", macs);
    }
}