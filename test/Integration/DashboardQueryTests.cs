using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Behavioural tests for the redesigned dashboard aggregate queries (SCR-003). Schema/column
/// validation for every [DatabaseCommand] is covered automatically by ServerQueryValidationTests;
/// these focus on the non-obvious logic: heartbeat liveness bucketing, fingerprint-recency
/// coverage, not-seen/new-device windows, posture predicates, and merged/alias exclusion.
/// </summary>
[Collection("Integration")]
public sealed class DashboardQueryTests
{
    private readonly IntegrationFixture _fx;

    public DashboardQueryTests(IntegrationFixture fx) => _fx = fx;

    private static readonly string[] AllTables =
    [
        "devices", "device_fingerprints", "device_aliases", "excluded_fingerprints",
        "agents", "agent_cycles", "services",
        "proj_disks", "proj_filesystems", "proj_containers", "proj_hardware_inventory",
        "proj_service_ca", "proj_devices", "proj_systems", "proj_hardware",
        "proj_discovered", "proj_device_arp",
        "incidents", "change_events",
    ];

    private async Task ResetAsync() => await _fx.TruncateAsync(AllTables);

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

    private async Task FingerprintAsync(Guid deviceId, string value, DateTimeOffset lastSeen) =>
        await ExecAsync(
            "INSERT INTO device_fingerprints (fp_type, fp_value, device_id, last_seen) VALUES ('mac', @v, @d, @ls)",
            ("v", value),
            ("d", deviceId),
            ("ls", lastSeen)
        );

    private async Task<Guid> AgentAsync(string status, DateTimeOffset? lastHeartbeat, int intervalSecs)
    {
        Guid id = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO agents (agent_id, hostname, api_key_hash, status, last_heartbeat, heartbeat_interval_secs)
            VALUES (@id, @h, @k, @s, @hb, @iv)
            """,
            ("id", id),
            ("h", $"agent-{id:N}"),
            ("k", Guid.NewGuid().ToString("N")),
            ("s", status),
            ("hb", lastHeartbeat),
            ("iv", intervalSecs)
        );
        return id;
    }

    // ── Network + coverage ──────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkSummary_counts_live_devices_and_coverage_excluding_aliases()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid m1 = await _fx.InsertDeviceAsync("managed");
        Guid m2 = await _fx.InsertDeviceAsync("managed");
        Guid d1 = await _fx.InsertDeviceAsync("discovered");
        Guid alias = await _fx.InsertDeviceAsync("discovered");
        await _fx.InsertAliasAsync(alias, m1); // merged away → must be excluded

        await FingerprintAsync(m1, "mac-1", now.AddMinutes(-5)); // reporting
        await FingerprintAsync(m2, "mac-2", now.AddHours(-2)); // reporting (<24h)
        await FingerprintAsync(d1, "mac-3", now.AddDays(-3)); // quiet
        await FingerprintAsync(alias, "mac-4", now); // excluded device

        await ExecAsync("INSERT INTO services (id, type) VALUES ('s1','dns'), ('s2','dns'), ('s3','http')");
        await AgentAsync("approved", now, 30);
        await ExecAsync("UPDATE agents SET zone = 'core'");
        await AgentAsync("approved", now, 30);
        await ExecAsync("UPDATE agents SET zone = 'edge' WHERE zone IS NULL");

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (long? Total, long? Managed, long? Discovered, long? Services, long? Zones, string? ZoneNames,
            long? Reporting, long? Quiet) r =
                await conn.GetNetworkSummaryAsync(CancellationToken.None).FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(3, r.Total); // alias excluded
        Assert.Equal(2, r.Managed);
        Assert.Equal(1, r.Discovered);
        Assert.Equal(3, r.Services);
        Assert.Equal(2, r.Zones);
        Assert.Equal("core, edge", r.ZoneNames);
        Assert.Equal(2, r.Reporting); // m1, m2 within 24h
        Assert.Equal(1, r.Quiet); // d1
    }

    // ── Recent activity ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ActivitySummary_and_lists_respect_windows()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid fresh = await _fx.InsertDeviceAsync("discovered", now.AddDays(-1)); // new (<7d)
        Guid old = await _fx.InsertDeviceAsync("managed", now.AddDays(-30)); // not new
        // A device with NO fingerprints must NOT be counted as "not seen" (count must match the list).
        await _fx.InsertDeviceAsync("managed", now.AddDays(-30));
        await FingerprintAsync(fresh, "mac-a", now.AddDays(-1)); // seen recently
        await FingerprintAsync(old, "mac-b", now.AddDays(-10)); // not seen >7d

        // changes_24h now counts incidents+change_events (curated), not raw facts_history rows.
        await ExecAsync(
            "INSERT INTO change_events (event_type, entity_kind, entity_id, occurred_at) VALUES ('discovered','device',@id,@t)",
            ("id", fresh.ToString()),
            ("t", now.AddHours(-1))
        );
        await ExecAsync(
            "INSERT INTO change_events (event_type, entity_kind, entity_id, occurred_at) VALUES ('discovered','device',@id,@t)",
            ("id", old.ToString()),
            ("t", now.AddDays(-2)) // outside the 24h window — must not count
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (long? NewD, long? NotSeen, long? Changes) a =
            await conn.GetActivitySummaryAsync(CancellationToken.None).FirstOrDefaultAsync(CancellationToken.None);
        Assert.Equal(1, a.NewD);
        Assert.Equal(1, a.NotSeen);
        Assert.Equal(1, a.Changes);

        List<(Guid DeviceId, string? Hostname, DateTimeOffset? LastSeen)> notSeen =
            await conn.GetNotSeenDevicesAsync(7, 10, CancellationToken.None).ToListAsync(CancellationToken.None);
        Assert.Single(notSeen);
        Assert.Equal(old, notSeen[0].DeviceId);

        List<(Guid DeviceId, string? Hostname, string ManagementStatus, DateTimeOffset CreatedAt)> newDevices =
            await conn.GetNewDevicesAsync(7, 10, CancellationToken.None).ToListAsync(CancellationToken.None);
        Assert.Single(newDevices);
        Assert.Equal(fresh, newDevices[0].DeviceId);
    }

    // ── Agent health / liveness ─────────────────────────────────────────────────

    [Fact]
    public async Task AgentHealthSummary_buckets_liveness_by_heartbeat_interval()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await AgentAsync("approved", now.AddSeconds(-10), 30); // online  (<= 3×30s)
        await AgentAsync("approved", now.AddMinutes(-5), 30); // stale   (>90s, <1h)
        await AgentAsync("approved", now.AddHours(-2), 30); // offline (>1h)
        await AgentAsync("pending", null, 30); // offline (never)

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (long? Total, long? Approved, long? Pending, long? Online, long? Stale, long? Offline) s =
            await conn.GetAgentHealthSummaryAsync(CancellationToken.None).FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(4, s.Total);
        Assert.Equal(3, s.Approved);
        Assert.Equal(1, s.Pending);
        Assert.Equal(1, s.Online);
        Assert.Equal(1, s.Stale);
        Assert.Equal(2, s.Offline);

        // Worst-first ordering: offline agents lead the list.
        List<(Guid AgentId, string Hostname, string Status, DateTimeOffset? LastHeartbeat, string? Zone,
            string? Version, string? PassiveDiscoveryMode, string? Liveness)> list =
            await conn.GetAgentHealthListAsync(10, CancellationToken.None).ToListAsync(CancellationToken.None);
        Assert.Equal(4, list.Count);
        Assert.Equal("offline", list[0].Liveness);
    }

    // ── Collection ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CollectionSummary_aggregates_latest_real_cycle_per_agent()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid a1 = await AgentAsync("approved", now, 30);
        Guid a2 = await AgentAsync("approved", now, 30);

        // a1: older real cycle then newer real cycle (newer wins); a2: single clean real cycle.
        await ExecAsync(
            """
            INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count, collectors)
            VALUES (@a,@t,100,50,5,'["ArpCollector"]'::jsonb)
            """,
            ("a", a1),
            ("t", now.AddMinutes(-10))
        );
        await ExecAsync(
            """
            INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count, collectors)
            VALUES (@a,@t,200,80,3,'["ArpCollector"]'::jsonb)
            """,
            ("a", a1),
            ("t", now.AddMinutes(-1))
        );
        await ExecAsync(
            """
            INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count, collectors)
            VALUES (@a,@t,300,20,0,'["ArpCollector"]'::jsonb)
            """,
            ("a", a2),
            ("t", now.AddMinutes(-2))
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (long? Facts, long? WithErrors, long? AvgMs, long? Reporting) s =
            await conn.GetCollectionSummaryAsync(CancellationToken.None).FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(100, s.Facts); // 80 (a1 latest) + 20 (a2)
        Assert.Equal(1, s.WithErrors); // only a1 latest has error_count>0
        Assert.Equal(250, s.AvgMs); // (200 + 300) / 2
        Assert.Equal(2, s.Reporting);
    }

    [Fact]
    public async Task CollectionSummary_skips_heartbeat_only_cycles_fleet_wide()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid agent = await AgentAsync("approved", now, 30);

        // A real discovery cycle 10 minutes ago carried hundreds of facts...
        await ExecAsync(
            """
            INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count, collectors)
            VALUES (@a,@t,83052,164,0,'["ArpCollector"]'::jsonb)
            """,
            ("a", agent),
            ("t", now.AddMinutes(-10))
        );
        // ...then the agent's most recent tick is heartbeat-only (empty collectors/scanners/
        // device_scanners/services). Picking the literal last row by cycle_at would report 0
        // facts sent fleet-wide even though a real cycle with facts ran minutes earlier.
        await ExecAsync(
            "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,10,0,0)",
            ("a", agent),
            ("t", now.AddMinutes(-1))
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (long? Facts, long? WithErrors, long? AvgMs, long? Reporting) s =
            await conn.GetCollectionSummaryAsync(CancellationToken.None).FirstOrDefaultAsync(CancellationToken.None);

        Assert.Equal(164, s.Facts);
        Assert.Equal(0, s.WithErrors);
        Assert.Equal(83052, s.AvgMs);
        Assert.Equal(1, s.Reporting);
    }

    [Fact]
    public async Task CollectionDailyFactsSent_sums_across_agents_by_day()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid a1 = await AgentAsync("approved", now, 30);
        Guid a2 = await AgentAsync("approved", now, 30);

        // Today: two agents both contribute.
        await ExecAsync(
            "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,100,30,0)",
            ("a", a1),
            ("t", now)
        );
        await ExecAsync(
            "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,100,15,0)",
            ("a", a2),
            ("t", now)
        );
        // Yesterday: only a1, plus a heartbeat-only tick contributing 0.
        await ExecAsync(
            "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,100,40,0)",
            ("a", a1),
            ("t", now.AddDays(-1))
        );
        await ExecAsync(
            "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,10,0,0)",
            ("a", a2),
            ("t", now.AddDays(-1))
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        Dictionary<DateTime, long> byDay = [];
        await foreach ((DateTimeOffset? Day, long? FactsSent) r in
            conn.GetCollectionDailyFactsSentAsync(2, CancellationToken.None))
        {
            byDay[r.Day!.Value.UtcDateTime.Date] = r.FactsSent ?? 0;
        }

        Assert.Equal(45, byDay[now.UtcDateTime.Date]); // 30 + 15
        Assert.Equal(40, byDay[now.AddDays(-1).UtcDateTime.Date]); // 40 + 0
    }

    [Fact]
    public async Task AgentCollectionSummary_skips_heartbeat_only_cycles_for_latest()
    {
        await ResetAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid agent = await AgentAsync("approved", now, 30);

        // A real discovery/inventory cycle 10 minutes ago...
        await ExecAsync(
            """
            INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count, collectors)
            VALUES (@a,@t,83052,164,0,'["ArpCollector"]'::jsonb)
            """,
            ("a", agent),
            ("t", now.AddMinutes(-10))
        );
        // ...then several heartbeat-only ticks since (empty collectors/scanners/device_scanners/services —
        // the agent's own default when a cycle runs neither discovery nor inventory).
        for (int i = 3; i >= 1; i--)
        {
            await ExecAsync(
                "INSERT INTO agent_cycles (agent_id, cycle_at, duration_ms, facts_sent, error_count) VALUES (@a,@t,10,0,0)",
                ("a", agent),
                ("t", now.AddMinutes(-i * 0.5))
            );
        }

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        (DateTimeOffset? LastCycleAt, int? LastFacts, int? LastErrors, int? LastDurationMs, int? WindowTotal, int?
            WindowErrored) r = await conn.GetAgentCollectionSummaryAsync(agent, now.AddHours(-1), CancellationToken.None)
            .FirstOrDefaultAsync(CancellationToken.None);

        // The tile must reflect the real cycle, not the most recent heartbeat-only tick.
        Assert.Equal(164, r.LastFacts);
        Assert.Equal(83052, r.LastDurationMs);
        Assert.Equal(4, r.WindowTotal); // window count still includes every cycle, heartbeats too
    }

    // ── Composition ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Composition_groups_live_devices_by_dimension()
    {
        await ResetAsync();
        Guid d1 = await _fx.InsertDeviceAsync("managed");
        Guid d2 = await _fx.InsertDeviceAsync("managed");
        Guid alias = await _fx.InsertDeviceAsync("discovered");
        await _fx.InsertAliasAsync(alias, d1);
        // A recently-seen fingerprint each, so they pass the liveness-window filter (visible_devices).
        await _fx.InsertFingerprintAsync(d1, "mac", "aa0011223301");
        await _fx.InsertFingerprintAsync(d2, "mac", "aa0011223302");
        await _fx.InsertFingerprintAsync(alias, "mac", "aa0011223303");

        await ExecAsync(
            "INSERT INTO proj_devices (device, kind, updated_at) VALUES (@d,'server',now())",
            ("d", d1.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_devices (device, kind, updated_at) VALUES (@d,'workstation',now())",
            ("d", d2.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_devices (device, kind, updated_at) VALUES (@d,'server',now())",
            ("d", alias.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_systems (device, os_family, updated_at) VALUES (@d,'linux',now())",
            ("d", d1.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_systems (device, os_family, updated_at) VALUES (@d,'linux',now())",
            ("d", d2.ToString())
        );
        // Vendor composition reads proj_hardware.system_vendor (the Devices device-maker source).
        await ExecAsync(
            "INSERT INTO proj_hardware (device, system_vendor, updated_at) VALUES (@d,'Dell',now())",
            ("d", d1.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_hardware (device, system_vendor, updated_at) VALUES (@d,'HP',now())",
            ("d", d2.ToString())
        );
        await ExecAsync(
            "INSERT INTO proj_hardware (device, system_vendor, updated_at) VALUES (@d,'Ghost',now())",
            ("d", alias.ToString())
        );

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        List<(string? OsFamily, long? Count)> byOs =
            await conn.GetCompositionByOsFamilyAsync(CancellationToken.None).ToListAsync(CancellationToken.None);
        List<(string? Kind, long? Count)> byKind =
            await conn.GetCompositionByKindAsync(CancellationToken.None).ToListAsync(CancellationToken.None);
        List<(string? Vendor, long? Count)> byVendor =
            await conn.GetCompositionByVendorAsync(CancellationToken.None).ToListAsync(CancellationToken.None);

        // Alias device excluded: 2 live devices, both linux.
        Assert.Contains(byOs, r => r.OsFamily == "linux" && r.Count == 2);
        // Kinds among live devices: one server (d1), one workstation (d2); alias 'server' excluded.
        Assert.Equal(1, byKind.Single(r => r.Kind == "server").Count);
        Assert.Equal(1, byKind.Single(r => r.Kind == "workstation").Count);
        // Vendor from proj_hardware.system_vendor; alias's 'Ghost' excluded.
        Assert.Equal(1, byVendor.Single(r => r.Vendor == "Dell").Count);
        Assert.Equal(1, byVendor.Single(r => r.Vendor == "HP").Count);
        Assert.DoesNotContain(byVendor, r => r.Vendor == "Ghost");
    }

    [Fact]
    public async Task Composition_by_discovery_source_counts_per_device_per_source()
    {
        await ResetAsync();
        Guid d1 = await _fx.InsertDeviceAsync("discovered");
        Guid d2 = await _fx.InsertDeviceAsync("managed");
        Guid alias = await _fx.InsertDeviceAsync("discovered");
        await _fx.InsertAliasAsync(alias, d2);

        // d1: stamped source 'arp' on its fingerprint AND seen by a network scanner (SshBannerScanner
        // → slug 'ssh-banner') via proj_discovered matched on the same MAC → counts in both buckets.
        await _fx.InsertFingerprintAsync(d1, "mac", "aa1122334455", "arp");
        await ExecAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, sources) VALUES ('obs-1', @ip, 'aa1122334455', 'SshBannerScanner')",
            ("ip", "192.168.1.10")
        );
        // d2: stamped source 'agent'.
        await _fx.InsertFingerprintAsync(d2, "mac", "bb1122334455", "agent");
        // alias: merged away → excluded from live_devices, so its 'arp' must not be counted.
        await _fx.InsertFingerprintAsync(alias, "mac", "cc1122334455", "arp");

        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        List<(string? Source, long? Count)> bySource =
            await conn.GetCompositionByDiscoverySourceAsync(CancellationToken.None).ToListAsync(CancellationToken.None);

        // 'arp' = d1 only (alias excluded); d1 also counts under the scanner slug; d2 under 'agent'.
        Assert.Equal(1, bySource.Single(r => r.Source == "arp").Count);
        Assert.Equal(1, bySource.Single(r => r.Source == "ssh-banner").Count);
        Assert.Equal(1, bySource.Single(r => r.Source == "agent").Count);
    }
}