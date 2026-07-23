using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Behavioural tests for the device liveness window: devices not seen within the configured window
/// are hidden from the visible_devices view (which the live inventory reads) but remain in
/// live_devices/devices (history is never lost). Also covers the settings round-trip. Per the
/// chosen policy the window hides BOTH managed and discovered devices when stale.
/// </summary>
[Collection("Integration")]
public sealed class DeviceLivenessWindowTests
{
    private readonly IntegrationFixture _fx;

    public DeviceLivenessWindowTests(IntegrationFixture fx) => _fx = fx;

    private async Task ResetAsync()
    {
        await _fx.TruncateAsync("devices", "device_fingerprints", "device_aliases");
        await SetWindowHoursAsync(24);
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

    private Task SetWindowHoursAsync(int hours) =>
        ExecAsync("UPDATE device_liveness_settings SET window_hours = @h WHERE id = TRUE", ("h", hours));

    /// <summary>Seeds a device with a single MAC fingerprint whose last_seen is <paramref name="ageHours" />
    /// hours ago (0 = seen just now). Stamps devices.last_seen alongside, matching the production
    /// invariant (every fingerprint sighting also advances the denormalized column — migration 0105).</summary>
    private async Task<Guid> SeedDeviceAsync(string managementStatus, double ageHours)
    {
        Guid id = await _fx.InsertDeviceAsync(managementStatus);
        await ExecAsync(
            "WITH fp AS (INSERT INTO device_fingerprints (fp_type, fp_value, device_id, source, last_seen) "
          + "VALUES ('mac', @v, @d, 'test', now() - make_interval(mins => @m))) "
          + "UPDATE devices SET last_seen = now() - make_interval(mins => @m) WHERE device_id = @d",
            ("v", Guid.NewGuid().ToString("N")[..12]),
            ("d", id),
            ("m", (int)(ageHours * 60))
        );
        return id;
    }

    private async Task<bool> IsInAsync(string relation, Guid id)
    {
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new($"SELECT EXISTS (SELECT 1 FROM {relation} WHERE device_id = @id)", conn);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync() ?? false);
    }

    [Fact]
    public async Task Fresh_device_is_visible()
    {
        await ResetAsync();
        Guid id = await SeedDeviceAsync("discovered", ageHours: 0);

        Assert.True(await IsInAsync("visible_devices", id));
    }

    [Fact]
    public async Task Stale_device_is_hidden_but_still_recorded()
    {
        await ResetAsync(); // window 24h
        Guid id = await SeedDeviceAsync("discovered", ageHours: 48);

        Assert.False(await IsInAsync("visible_devices", id)); // hidden from live inventory
        Assert.True(await IsInAsync("live_devices", id));     // but never deleted — still in history
    }

    [Fact]
    public async Task Window_is_configurable()
    {
        await ResetAsync();
        Guid id = await SeedDeviceAsync("discovered", ageHours: 48);

        await SetWindowHoursAsync(24);
        Assert.False(await IsInAsync("visible_devices", id));

        await SetWindowHoursAsync(72); // widen past the device's age
        Assert.True(await IsInAsync("visible_devices", id));
    }

    [Fact]
    public async Task Managed_and_discovered_are_both_hidden_when_stale()
    {
        await ResetAsync(); // window 24h
        Guid managed = await SeedDeviceAsync("managed", ageHours: 48);
        Guid discovered = await SeedDeviceAsync("discovered", ageHours: 48);

        Assert.False(await IsInAsync("visible_devices", managed));
        Assert.False(await IsInAsync("visible_devices", discovered));
    }

    [Fact]
    public async Task Reappearing_device_unhides_automatically()
    {
        await ResetAsync(); // window 24h
        Guid id = await _fx.InsertDeviceAsync("discovered");
        // First sighting is stale... (fingerprint + devices.last_seen stamped together, as
        // production always does — migration 0105)
        await ExecAsync(
            "WITH fp AS (INSERT INTO device_fingerprints (fp_type, fp_value, device_id, source, last_seen) "
          + "VALUES ('mac', @v, @d, 'test', now() - interval '48 hours')) "
          + "UPDATE devices SET last_seen = now() - interval '48 hours' WHERE device_id = @d",
            ("v", "aaaaaaaaaaaa"),
            ("d", id)
        );
        Assert.False(await IsInAsync("visible_devices", id));

        // ...then it is seen again (last_seen bumped, as UpsertFingerprints does every resolution).
        await ExecAsync(
            "WITH fp AS (UPDATE device_fingerprints SET last_seen = now() WHERE device_id = @d) "
          + "UPDATE devices SET last_seen = GREATEST(last_seen, now()) WHERE device_id = @d",
            ("d", id)
        );
        Assert.True(await IsInAsync("visible_devices", id));
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        await SetWindowHoursAsync(24);
        await using NpgsqlConnection conn = await _fx.DataSource.OpenConnectionAsync();

        Assert.Equal(
            24,
            (await conn.GetDeviceLivenessSettingsAsync(CancellationToken.None).FirstAsync(CancellationToken.None))
                .WindowHours
        );

        int updated = (await conn.UpdateDeviceLivenessSettingsAsync(48, CancellationToken.None)
            .FirstAsync(CancellationToken.None)).WindowHours;
        Assert.Equal(48, updated);
        Assert.Equal(
            48,
            (await conn.GetDeviceLivenessSettingsAsync(CancellationToken.None).FirstAsync(CancellationToken.None))
                .WindowHours
        );
    }
}