using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Covers the retention-consolidation + cache-invalidation work: the consolidated retention tiers
/// (migration 0101), the passive-device last_seen re-stamp (StampObservedMacLastSeen), and the
/// forced-re-collect due-agent selection (GetAgentsDueForRecollect).
/// </summary>
[Collection("Integration")]
public sealed class RetentionAndRecollectTests
{
    private readonly IntegrationFixture _fx;

    public RetentionAndRecollectTests(IntegrationFixture fx) => _fx = fx;

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

    private async Task<(string Category, TimeSpan? StaleAfter)> PolicyAsync(string table)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT category, stale_after FROM retention_policies WHERE table_name = @t",
            conn
        );
        cmd.Parameters.AddWithValue("t", table);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"no retention policy row for {table}");
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetFieldValue<TimeSpan>(1));
    }

    private async Task<DateTime> LastSeenAsync(string fpValue)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT last_seen FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = @v",
            conn
        );
        cmd.Parameters.AddWithValue("v", fpValue);
        object v = await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("no fingerprint");
        return v is DateTimeOffset dto ? dto.UtcDateTime : ((DateTime)v).ToUniversalTime();
    }

    // ── Retention consolidation (migration 0101) ──────────────────────────────

    [Fact]
    public async Task Retention_tiers_are_consolidated()
    {
        Assert.Equal(("ephemeral", TimeSpan.FromDays(2)), await PolicyAsync("proj_device_arp"));
        Assert.Equal(("ephemeral", TimeSpan.FromDays(2)), await PolicyAsync("proj_dhcp_local_leases"));
        // The acute fix: identity facts were emptying at 7d; now on the 30d steady tier.
        Assert.Equal(("steady", TimeSpan.FromDays(30)), await PolicyAsync("materialization_facts"));
        Assert.Equal(("steady", TimeSpan.FromDays(30)), await PolicyAsync("proj_systems"));
        Assert.Equal(("steady", TimeSpan.FromDays(30)), await PolicyAsync("proj_discovered"));
        // History unchanged.
        Assert.Equal(("history", TimeSpan.FromDays(90)), await PolicyAsync("facts_history"));
    }

    // ── Passive-device liveness re-stamp ──────────────────────────────────────

    [Fact]
    public async Task Stamp_advances_present_mac_but_not_departed_or_backward()
    {
        await _fx.TruncateAsync("devices", "device_fingerprints", "proj_device_arp");

        // Present device: fingerprint went stale, but its MAC is freshly observed in ARP → bump.
        Guid present = await _fx.InsertDeviceAsync("discovered");
        await SeedMacAsync(present, "001122330011", fpAgeHours: 48);
        await SeedArpAsync("001122330011", observedAgeMinutes: 0);

        // Departed device: fingerprint fresh, ARP observation is older → must NOT move backward.
        Guid departed = await _fx.InsertDeviceAsync("discovered");
        await SeedMacAsync(departed, "001122330022", fpAgeHours: 0);
        await SeedArpAsync("001122330022", observedAgeMinutes: 180);

        await using (NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync())
        {
            await foreach (TouchedDeviceResult _ in conn.StampObservedMacLastSeenAsync(CancellationToken.None))
            {
                // drain to drive execution
            }
        }

        Assert.True(await LastSeenAsync("001122330011") > DateTime.UtcNow - TimeSpan.FromMinutes(5),
            "present device's last_seen should advance to the fresh ARP sighting");
        Assert.True(await LastSeenAsync("001122330022") > DateTime.UtcNow - TimeSpan.FromMinutes(5),
            "departed device's fresh last_seen must not be dragged backward to the older ARP sighting");
    }

    private Task SeedMacAsync(Guid device, string mac, double fpAgeHours) =>
        ExecAsync(
            "INSERT INTO device_fingerprints (fp_type, fp_value, device_id, source, last_seen) "
          + "VALUES ('mac', @v, @d, 'test', now() - make_interval(mins => @m))",
            ("v", mac), ("d", device), ("m", (int)(fpAgeHours * 60))
        );

    private Task SeedArpAsync(string mac, int observedAgeMinutes) =>
        ExecAsync(
            "INSERT INTO proj_device_arp (device, arp, mac, iface, state, updated_at) "
          + "VALUES (@d, '10.0.0.5', @mac, 'eth0', 'reachable', now() - make_interval(mins => @m))",
            ("d", Guid.NewGuid().ToString()), ("mac", mac), ("m", observedAgeMinutes)
        );

    // ── Forced re-collect due-agent selection ─────────────────────────────────

    [Fact]
    public async Task Due_agents_respect_cadence_limit_and_ordering()
    {
        await _fx.TruncateAsync("agents"); // retention_policies (migration-seeded steady rows) left intact

        // Cadence = min steady retention (30d) / 4 = 7.5d.
        Guid never = await InsertApprovedAgentAsync();  // clear_trackers_requested_at NULL → due
        Guid stale = await InsertApprovedAgentAsync();  // cleared 10d ago → due
        Guid recent = await InsertApprovedAgentAsync(); // cleared 1d ago → NOT due
        await SetClearedAsync(stale, daysAgo: 10);
        await SetClearedAsync(recent, daysAgo: 1);

        List<Guid> due = await DueAsync(maxAgents: 10);
        Assert.Contains(never, due);
        Assert.Contains(stale, due);
        Assert.DoesNotContain(recent, due);

        // Oldest-first (NULL first), capped at the limit.
        List<Guid> capped = await DueAsync(maxAgents: 1);
        Assert.Equal(new[] { never }, capped);
    }

    private async Task<Guid> InsertApprovedAgentAsync()
    {
        Guid id = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO agents (agent_id, hostname, api_key_hash, status, version) "
          + "VALUES (@id, 'test-agent', @hash, 'approved', '0.0.0')",
            ("id", id), ("hash", id.ToString("N")) // unique per agent (agents_api_key_hash_idx)
        );
        return id;
    }

    private Task SetClearedAsync(Guid agent, int daysAgo) =>
        ExecAsync(
            "UPDATE agents SET clear_trackers_requested_at = now() - make_interval(days => @d) WHERE agent_id = @a",
            ("d", daysAgo), ("a", agent)
        );

    private async Task<List<Guid>> DueAsync(int maxAgents)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        List<Guid> ids = [];
        await foreach (AgentIdResult r in conn.GetAgentsDueForRecollectAsync(maxAgents, CancellationToken.None))
        {
            ids.Add(r.AgentId);
        }

        return ids;
    }
}