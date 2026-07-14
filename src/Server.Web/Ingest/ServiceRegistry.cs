using JMW.Discovery.Core;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Resolves or mints stable ServiceIds for logical service entities (DNS servers,
/// monitoring platforms, etc.) based on their fingerprints.
/// Mirrors DeviceRegistry but for services rather than physical/virtual hosts.
/// A service is identified by what it manages (e.g. DNS zones) rather than
/// hardware properties, so it survives host migration.
/// Matching strategy: if ANY fingerprint matches an existing service of the same
/// type, it's the same service. This is more lenient than device matching because
/// services are expected to gain/lose zones over time. A majority-match rule would
/// be too strict for services that start with few zones.
/// </summary>
public sealed class ServiceRegistry
{
    private readonly NpgsqlDataSource _db;

    public ServiceRegistry(NpgsqlDataSource db) => _db = db;

    /// <summary>
    /// Returns the stable ServiceId for the described service, creating one if needed.
    /// </summary>
    public async Task<(string ServiceId, bool IsNew)> IdentifyAsync(
        ServiceIdentifyRequest request,
        CancellationToken ct
    )
    {
        if (request.Probe.Fingerprints.Count == 0)
        {
            throw new ArgumentException("At least one service fingerprint is required.");
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        // Try to find an existing service by matching any fingerprint.
        string? existing = await FindByFingerprintAsync(
            conn,
            request.Probe.ServiceType,
            request.Probe.Fingerprints,
            ct
        );

        if (existing is not null)
        {
            // Upsert any new fingerprints we learned this call.
            await UpsertFingerprintsAsync(conn, existing, request.Probe.Fingerprints, ct);
            return (existing, IsNew: false);
        }

        // No match — create a new service record.
        string serviceId = Guid.NewGuid().ToString("D");
        await CreateServiceAsync(conn, serviceId, request.Probe, ct);
        return (serviceId, IsNew: true);
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    private static async Task<string?> FindByFingerprintAsync(
        NpgsqlConnection conn,
        string serviceType,
        IReadOnlyList<ServiceFingerprint> fingerprints,
        CancellationToken ct
    )
    {
        // Match on any fingerprint value for this service type.
        // We use unnest to pass the full list in one query.
        const string sql = """
            SELECT sf.service_id
            FROM   service_fingerprints sf
            JOIN   services s ON s.id = sf.service_id
            WHERE  s.type          = $1
              AND  sf.fp_type      = ANY($2::text[])
              AND  sf.fp_value     = ANY($3::text[])
            LIMIT  1
            """;

        string[] types = fingerprints.Select(f => f.Type).ToArray();
        string[] values = fingerprints.Select(f => f.Value).ToArray();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(serviceType);
        cmd.Parameters.AddWithValue(types);
        cmd.Parameters.AddWithValue(values);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // ── Creation ──────────────────────────────────────────────────────────────

    private static async Task CreateServiceAsync(
        NpgsqlConnection conn,
        string serviceId,
        ServiceProbe probe,
        CancellationToken ct
    )
    {
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (NpgsqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO services (id, type, created_at)
                    VALUES ($1, $2, now())
                    ON CONFLICT (id) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue(serviceId);
                cmd.Parameters.AddWithValue(probe.ServiceType);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await InsertFingerprintsAsync(conn, serviceId, probe.Fingerprints, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task InsertFingerprintsAsync(
        NpgsqlConnection conn,
        string serviceId,
        IReadOnlyList<ServiceFingerprint> fingerprints,
        CancellationToken ct
    )
    {
        // Single batched insert instead of one round-trip per fingerprint — same unnest()
        // pattern GenericProjection.BuildSql uses for its own multi-row upserts. unnest() calls
        // in a FROM-less SELECT list zip element-wise (fp_type[i] with fp_value[i]), not a
        // cross product, so this produces exactly one row per fingerprint.
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO service_fingerprints (service_id, fp_type, fp_value)
            SELECT $1, unnest($2::text[]), unnest($3::text[])
            ON CONFLICT (service_id, fp_type, fp_value) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(serviceId);
        cmd.Parameters.AddWithValue(fingerprints.Select(f => f.Type).ToArray());
        cmd.Parameters.AddWithValue(fingerprints.Select(f => f.Value).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertFingerprintsAsync(
        NpgsqlConnection conn,
        string serviceId,
        IReadOnlyList<ServiceFingerprint> fingerprints,
        CancellationToken ct
    ) =>
        await InsertFingerprintsAsync(conn, serviceId, fingerprints, ct);
}