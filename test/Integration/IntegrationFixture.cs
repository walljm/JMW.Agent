using DotNet.Testcontainers.Builders;

using Npgsql;

using Testcontainers.PostgreSql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Starts a single Postgres container for all integration tests in the "Integration" collection.
/// The production migration chain is applied once on startup (via <see cref="MigrationTestRunner" />),
/// so tests run against the exact schema production runs. Each test is responsible for
/// truncating the tables it touches before seeding.
/// </summary>
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture> { }

public sealed class IntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer _pg = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder()
            .WithDatabase("discovery_test")
            .WithUsername("test")
            .WithPassword("test")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();

        await _pg.StartAsync();

        // Match production: the app connects with search_path=jmwdiscovery,public and the
        // migrations create every object in the jmwdiscovery schema.
        string connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            SearchPath = "jmwdiscovery,public",
        }.ConnectionString;

        DataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

        await MigrationTestRunner.ApplyAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _pg.DisposeAsync();
    }

    public async Task TruncateAsync(params string[] tables)
    {
        string list = string.Join(", ", tables);
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new($"TRUNCATE TABLE {list} CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a row into devices and returns the generated device_id.
    /// </summary>
    public async Task<Guid> InsertDeviceAsync(
        string managementStatus,
        DateTimeOffset? createdAt = null
    )
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO devices (device_id, management_status, created_at)
            VALUES (@id, @status, @createdAt)
            """;
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", managementStatus);
        cmd.Parameters.AddWithValue("createdAt", createdAt ?? DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// Inserts a device_fingerprint row.
    /// </summary>
    public async Task InsertFingerprintAsync(
        Guid deviceId,
        string fpType,
        string fpValue,
        string source = "test"
    )
    {
        // PK is (fp_type, fp_value) — one device per fingerprint.
        const string sql = """
            INSERT INTO device_fingerprints (fp_type, fp_value, device_id, source, last_seen)
            VALUES (@type, @value, @id, @source, now())
            ON CONFLICT (fp_type, fp_value) DO NOTHING
            """;
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("type", fpType);
        cmd.Parameters.AddWithValue("value", fpValue);
        cmd.Parameters.AddWithValue("id", deviceId);
        cmd.Parameters.AddWithValue("source", source);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Counts rows in a table with an optional WHERE clause.
    /// </summary>
    public async Task<long> CountAsync(string table, string? where = null)
    {
        string sql = $"SELECT COUNT(*) FROM {table}" + (where is not null ? $" WHERE {where}" : "");
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is long l ? l : Convert.ToInt64(result);
    }

    /// <summary>
    /// Inserts a device_aliases row (loser → survivor).
    /// </summary>
    public async Task InsertAliasAsync(Guid loser, Guid survivor)
    {
        const string sql = """
            INSERT INTO device_aliases (alias_device_id, survivor_device_id)
            VALUES (@loser, @survivor)
            ON CONFLICT (alias_device_id) DO UPDATE SET survivor_device_id = EXCLUDED.survivor_device_id
            """;
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("loser", loser);
        cmd.Parameters.AddWithValue("survivor", survivor);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts a row into excluded_fingerprints.
    /// </summary>
    public async Task InsertExcludedFingerprintAsync(string fpType, string fpValue)
    {
        const string sql = """
            INSERT INTO excluded_fingerprints (fp_type, fp_value)
            VALUES (@type, @value)
            ON CONFLICT DO NOTHING
            """;
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("type", fpType);
        cmd.Parameters.AddWithValue("value", fpValue);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts a minimal agent row and returns the agent_id.
    /// </summary>
    public async Task<Guid> InsertAgentAsync()
    {
        Guid id = Guid.NewGuid();
        const string sql = """
            INSERT INTO agents (agent_id, hostname, api_key_hash, status, version)
            VALUES (@id, 'test-agent', 'hash', 'approved', '0.0.0')
            """;
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// Promotes a device: inserts a collection target and updates management_status to 'managed'.
    /// Returns the new target_id.
    /// </summary>
    public async Task<Guid> PromoteDeviceAsync(
        Guid deviceId,
        Guid agentId,
        string address,
        string protocol
    )
    {
        Guid targetId = Guid.NewGuid();
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();

        await using (NpgsqlCommand insertTarget = new(
            """
            INSERT INTO targets (target_id, agent_id, endpoint, collector_type)
            VALUES (@targetId, @agentId, @address, @protocol)
            """,
            conn,
            tx
        ))
        {
            insertTarget.Parameters.AddWithValue("targetId", targetId);
            insertTarget.Parameters.AddWithValue("agentId", agentId);
            insertTarget.Parameters.AddWithValue("address", address);
            insertTarget.Parameters.AddWithValue("protocol", protocol);
            await insertTarget.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand updateDevice = new(
            """
            UPDATE devices SET management_status = 'managed', updated_at = now()
            WHERE device_id = @deviceId
            """,
            conn,
            tx
        ))
        {
            updateDevice.Parameters.AddWithValue("deviceId", deviceId);
            await updateDevice.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return targetId;
    }
}