using System.Diagnostics.CodeAnalysis;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Resolves a stable DeviceId from a set of fingerprints, with auto-merge.
/// Resolution algorithm:
/// 0 matches → create new device with requested managementStatus, store fingerprints
/// 1 match   → associate any new fingerprints; update last_seen + source
/// 2+ matches → auto-merge: survivor = oldest device (MIN created_at), losers are aliased
/// Excluded fingerprints (excluded_fingerprints table) are skipped during matching.
/// All merge operations run in a single transaction with the lookup reader closed first.
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public sealed class DeviceRegistry
{
    private readonly NpgsqlDataSource _db;

    public DeviceRegistry(NpgsqlDataSource db)
    {
        _db = db;
    }

    /// <summary>
    /// Resolves or creates a device from the given fingerprints.
    /// </summary>
    public async Task<(string DeviceId, bool IsNew)> ResolveAsync(
        IReadOnlyList<Fingerprint> fingerprints,
        string source,
        string managementStatus = "managed",
        CancellationToken ct = default
    )
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        return await ResolveWithConnectionAsync(conn, fingerprints, source, managementStatus, ct);
    }

    /// <summary>
    /// Same as ResolveAsync but reuses an already-open connection.
    /// Callers that already hold a connection (e.g. DiscoveryMaterializer after reading rows)
    /// should prefer this overload to avoid opening a new connection per device.
    /// </summary>
    internal static async Task<(string DeviceId, bool IsNew)> ResolveWithConnectionAsync(
        NpgsqlConnection conn,
        IReadOnlyList<Fingerprint> fingerprints,
        string source,
        string managementStatus = "managed",
        CancellationToken ct = default
    )
    {
        List<Fingerprint> normalized = NormalizeAll(fingerprints);
        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                "No valid fingerprints after normalization. At least one valid fingerprint is required.",
                nameof(fingerprints)
            );
        }

        // Collect all device_ids that match AND are not excluded — reader is closed before writes.
        List<string> matchedIds = await FindMatchingDeviceIdsAsync(conn, normalized, ct);

        (string DeviceId, bool IsNew) result = matchedIds.Count switch
        {
            0 => await CreateDeviceAsync(conn, normalized, source, managementStatus, ct),
            1 => await UpdateFingerprintsAsync(conn, matchedIds[0], normalized, source, managementStatus, isNew: false, ct),
            _ => await AutoMergeAsync(conn, matchedIds, normalized, source, managementStatus, ct),
        };

        if (result.IsNew)
        {
            // "discovered" change event — every branch above has already committed its own
            // transaction, so this write is safely outside any of them.
            await conn.InsertChangeEventAsync("discovered", "device", result.DeviceId, detail: null, ct)
                .ExecuteAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Checks whether deviceId is a merged-away alias and returns the survivor's id if so.
    /// Returns the original id when it is not an alias.
    /// </summary>
    public async Task<string> ResolveAliasAsync(string deviceId, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT survivor_device_id::text FROM device_aliases WHERE alias_device_id = $1::uuid";
        cmd.Parameters.Add(Param.Text(deviceId));

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result as string ?? deviceId;
    }

    // ── Legacy identify API (kept for existing callers until fully removed) ────

    public async Task<DeviceIdentifyResponse> IdentifyAsync(
        DeviceIdentifyRequest request,
        CancellationToken ct = default
    )
    {
        (string deviceId, bool isNew) = await ResolveAsync(request.Fingerprints, source: "legacy", ct: ct);
        return new DeviceIdentifyResponse(deviceId, isNew);
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    internal static List<Fingerprint> NormalizeAll(IReadOnlyList<Fingerprint> raw)
    {
        List<Fingerprint> result = new(raw.Count);
        foreach (Fingerprint fp in raw)
        {
            string? normalized = fp.Type is FingerprintType.ChassisSerial or FingerprintType.DiskSerial
                ? TryNormalizeSerialWithEmbeddedVendor(fp.Value)
                : FingerprintNormalizer.Normalize(fp.Type, fp.Value);

            if (normalized is not null)
            {
                result.Add(new Fingerprint(fp.Type, normalized));
            }
        }

        return result;
    }

    // Collectors may send serials pre-scoped ("cisco:FTX1234") or unscoped ("FTX1234").
    private static string? TryNormalizeSerialWithEmbeddedVendor(string raw)
    {
        string trimmed = raw.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            string vendor = trimmed[..colon];
            string serial = trimmed[(colon + 1)..];
            return FingerprintNormalizer.NormalizeSerial(serial, vendor);
        }

        return FingerprintNormalizer.NormalizeSerial(trimmed, "bare");
    }

    // ── DB operations ─────────────────────────────────────────────────────────

    private static async Task<List<string>> FindMatchingDeviceIdsAsync(
        NpgsqlConnection conn,
        List<Fingerprint> fingerprints,
        CancellationToken ct
    )
    {
        // Skip any (fp_type, fp_value) pairs in excluded_fingerprints.
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT df.device_id::text
            FROM device_fingerprints df
            WHERE (df.fp_type, df.fp_value) = ANY(
                SELECT * FROM unnest($1::text[], $2::text[])
            )
            AND NOT EXISTS (
                SELECT 1 FROM excluded_fingerprints ef
                WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
            )
            """;
        string[] fpTypes = new string[fingerprints.Count];
        string[] fpValues = new string[fingerprints.Count];
        for (int i = 0; i < fingerprints.Count; i++)
        {
            fpTypes[i] = fingerprints[i].Type;
            fpValues[i] = fingerprints[i].Value;
        }

        cmd.Parameters.Add(Param.TextArray(fpTypes));
        cmd.Parameters.Add(Param.TextArray(fpValues));

        List<string> ids = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        // Reader closed (disposed) before returning — caller may issue writes.
        return ids;
    }

    private static async Task<(string DeviceId, bool IsNew)> CreateDeviceAsync(
        NpgsqlConnection conn,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        CancellationToken ct
    )
    {
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        string deviceId;
        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO devices (management_status) VALUES ($1) RETURNING device_id::text";
            cmd.Parameters.Add(Param.Text(managementStatus));
            deviceId = await cmd.ExecuteScalarAsync(ct) is string s
                ? s
                : throw new InvalidOperationException("INSERT INTO devices returned no device_id.");
        }

        await UpsertFingerprintsAsync(conn, tx, deviceId, fingerprints, source, ct);
        await tx.CommitAsync(ct);

        return (deviceId, IsNew: true);
    }

    private static async Task<(string DeviceId, bool IsNew)> UpdateFingerprintsAsync(
        NpgsqlConnection conn,
        string deviceId,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        bool isNew,
        CancellationToken ct
    )
    {
        await UpsertFingerprintsAsync(conn, null, deviceId, fingerprints, source, ct);
        await PromoteToManagedAsync(conn, null, deviceId, managementStatus, ct);
        return (deviceId, isNew);
    }

    private static async Task<(string DeviceId, bool IsNew)> AutoMergeAsync(
        NpgsqlConnection conn,
        List<string> deviceIds,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        CancellationToken ct
    )
    {
        // Read the survivor before starting writes (reader must be closed first).
        string survivor = await FindOldestDeviceIdAsync(conn, deviceIds, ct);
        List<string> losers = deviceIds.Where(id => id != survivor).ToList();

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        await MergeLosersAsync(
            conn,
            tx,
            survivor,
            [.. losers],
            actor: "system:ingest",
            auditAction: "device.auto_merge",
            auditDetail: new { merged = losers },
            ct
        );

        await UpsertFingerprintsAsync(conn, tx, survivor, fingerprints, source, ct);
        await PromoteToManagedAsync(conn, tx, survivor, managementStatus, ct);
        await tx.CommitAsync(ct);

        return (survivor, IsNew: false);
    }

    /// <summary>
    /// Promotes an existing "discovered" device to "managed" when a resolve call arrives
    /// requesting managed status — e.g. a device first seen passively (ARP/scanner) that a
    /// collector with real credentials (SSH, SNMP, google-wifi, ...) later resolves onto via
    /// agent-side probing (ICollectionContext.RegisterProbeAsync). One-directional: never
    /// downgrades an already-managed device, and a no-op for any other requested status
    /// (passive collectors pass "discovered" and should never touch an existing device's
    /// management status).
    /// </summary>
    private static async Task PromoteToManagedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string deviceId,
        string managementStatus,
        CancellationToken ct
    )
    {
        if (managementStatus != "managed")
        {
            return;
        }

        await using NpgsqlCommand cmd = conn.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        cmd.CommandText = """
            UPDATE devices SET management_status = 'managed', updated_at = now()
            WHERE device_id = $1::uuid AND management_status = 'discovered'
            """;
        cmd.Parameters.Add(Param.Text(deviceId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Projection tables holding a per-device current-state snapshot, keyed by the column named
    /// here. Rebuilt from live facts as they arrive, so once a loser's fingerprints are reassigned
    /// (see <see cref="MergeLosersAsync"/>) nothing will ever refresh its rows again — they become
    /// permanently stale and are purged on merge rather than left as an orphaned duplicate of the
    /// survivor's data.
    /// </summary>
    private static readonly (string Table, string Column)[] DeviceProjectionTables =
    [
        ("proj_containers", "device"),
        ("proj_device_arp", "device"),
        ("proj_device_routes", "device"),
        ("proj_devices", "device"),
        ("proj_dhcp_local_leases", "device"),
        ("proj_discovered", "device"),
        ("proj_discovered_services", "device"),
        ("proj_disks", "device"),
        ("proj_filesystems", "device"),
        ("proj_hardware", "device"),
        ("proj_hardware_inventory", "device"),
        ("proj_interfaces", "device"),
        ("proj_ports", "device"),
        ("proj_services", "device_id"),
        ("proj_systems", "device"),
    ];

    /// <summary>
    /// Aliases every loser to survivor, reassigns their fingerprints, appends them to
    /// survivor's merged_from (idempotent — losers already recorded are skipped, so retrying
    /// the same merge is safe), purges the losers' stale projection rows, and deletes the losers'
    /// own devices rows so nothing of them is independently browsable afterward — then writes one
    /// audit entry. Shared by <see cref="AutoMergeAsync"/> (N losers from fingerprint collision)
    /// and <see cref="ManualMergeAsync"/> (1 loser from an admin action) — the two had diverged
    /// (array_cat vs array_append+guard) before unifying here on the guarded, retry-safe form.
    /// </summary>
    private static async Task MergeLosersAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string survivor,
        string[] losers,
        string actor,
        string auditAction,
        object auditDetail,
        CancellationToken ct
    )
    {
        // Alias INSERT: one row per loser → survivor.
        await using (NpgsqlCommand aliasCmd = conn.CreateCommand())
        {
            aliasCmd.Transaction = tx;
            aliasCmd.CommandText = """
                INSERT INTO device_aliases (alias_device_id, survivor_device_id)
                SELECT unnest($1::uuid[]), $2::uuid
                ON CONFLICT (alias_device_id) DO UPDATE SET survivor_device_id = EXCLUDED.survivor_device_id
                """;
            aliasCmd.Parameters.Add(Param.TextArray(losers));
            aliasCmd.Parameters.Add(Param.Text(survivor));
            await aliasCmd.ExecuteNonQueryAsync(ct);
        }

        // Fingerprint UPDATE: reassign all loser fingerprints to survivor in one query.
        await using (NpgsqlCommand fpCmd = conn.CreateCommand())
        {
            fpCmd.Transaction = tx;
            fpCmd.CommandText = """
                UPDATE device_fingerprints
                SET device_id = $2::uuid
                WHERE device_id = ANY($1::uuid[])
                  AND (fp_type, fp_value) NOT IN (
                      SELECT fp_type, fp_value FROM device_fingerprints WHERE device_id = $2::uuid
                  )
                """;
            fpCmd.Parameters.Add(Param.TextArray(losers));
            fpCmd.Parameters.Add(Param.Text(survivor));
            await fpCmd.ExecuteNonQueryAsync(ct);
        }

        // Repoint historical facts (own AND discovered/sighting rows) from losers to survivor.
        // ADR-002 documents this repoint as part of merge, but it was never actually
        // implemented — left alone, a loser's entire fact history becomes permanently
        // unreachable the moment its fingerprints move above (nothing ever queries a
        // merged-away device_id again). facts_history and metrics_raw share the same
        // id/key_values shape (id embeds "Device[{deviceId}]" as a literal prefix; key_values
        // carries the same id again under the "Device" key), so both need the same rewrite.
        foreach (string table in new[] { "facts_history", "metrics_raw" })
        {
            await using NpgsqlCommand repointCmd = conn.CreateCommand();
            repointCmd.Transaction = tx;
            repointCmd.CommandText = $"UPDATE {table} SET " + """
                id = 'Device[' || $2 || ']' || substring(id FROM length(key_values ->> 'Device') + 9),
                key_values = jsonb_set(key_values, '{Device}', to_jsonb($2::text))
                WHERE key_values ->> 'Device' = ANY($1::text[])
                """;
            repointCmd.Parameters.Add(Param.TextArray(losers));
            repointCmd.Parameters.Add(Param.Text(survivor));
            await repointCmd.ExecuteNonQueryAsync(ct);
        }

        // merged_from UPDATE: append losers not already recorded, deduplicated — safe to
        // retry the same merge (auto-merge re-running on the same batch, or a repeated
        // manual-merge call) without accumulating duplicate entries.
        await using (NpgsqlCommand mergeCmd = conn.CreateCommand())
        {
            mergeCmd.Transaction = tx;
            mergeCmd.CommandText = """
                UPDATE devices
                SET merged_from = (SELECT array_agg(DISTINCT e) FROM unnest(merged_from || $2::uuid[]) e),
                    updated_at  = now()
                WHERE device_id = $1::uuid
                """;
            mergeCmd.Parameters.Add(Param.Text(survivor));
            mergeCmd.Parameters.Add(Param.TextArray(losers));
            await mergeCmd.ExecuteNonQueryAsync(ct);
        }

        // Purge each loser's stale projection rows — the survivor's own rows already hold (or
        // will hold, from future facts) the merged current-state view.
        foreach ((string table, string column) in DeviceProjectionTables)
        {
            await using NpgsqlCommand purgeCmd = conn.CreateCommand();
            purgeCmd.Transaction = tx;
            purgeCmd.CommandText = $"DELETE FROM {table} WHERE {column} = ANY($1::text[])";
            purgeCmd.Parameters.Add(Param.TextArray(losers));
            await purgeCmd.ExecuteNonQueryAsync(ct);
        }

        // Retire the losers' own devices rows. Safe: their fingerprints were just reassigned
        // above (device_fingerprints has an FK back to devices with no cascade, so this throws
        // instead of silently orphaning data if any fingerprint was somehow left behind), and
        // device_aliases.alias_device_id is deliberately not FK'd to devices so the alias — and
        // ResolveAliasAsync's redirect — survives the loser row's deletion.
        await using (NpgsqlCommand deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM devices WHERE device_id = ANY($1::uuid[])";
            deleteCmd.Parameters.Add(Param.TextArray(losers));
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        await AuditLog.WriteAsync(conn, tx, actor, auditAction, survivor, auditDetail, ct);
    }

    private static async Task<string> FindOldestDeviceIdAsync(
        NpgsqlConnection conn,
        List<string> deviceIds,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT device_id::text FROM devices
            WHERE device_id = ANY($1::uuid[])
            ORDER BY created_at ASC
            LIMIT 1
            """;
        cmd.Parameters.Add(Param.TextArray(deviceIds.ToArray()));

        return await cmd.ExecuteScalarAsync(ct) is string id
            ? id
            : throw new InvalidOperationException("Could not find oldest device from provided IDs.");
    }

    private static async Task UpsertFingerprintsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string deviceId,
        List<Fingerprint> fingerprints,
        string source,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        cmd.CommandText = """
            INSERT INTO device_fingerprints (fp_type, fp_value, device_id, first_seen, last_seen, source)
            SELECT * FROM unnest($1::text[], $2::text[], $3::uuid[], $4::timestamptz[], $5::timestamptz[], $6::text[])
              AS t(fp_type, fp_value, device_id, first_seen, last_seen, source)
            ON CONFLICT (fp_type, fp_value) DO UPDATE
              SET last_seen  = EXCLUDED.last_seen,
                  source     = EXCLUDED.source
            """;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Deduplicate by (fp_type, fp_value): the input may contain the same
        // fingerprint twice — e.g. a host where a bond/bridge interface shares its
        // MAC with the enslaved physical NIC. Feeding duplicate conflict keys into a
        // single "ON CONFLICT (fp_type, fp_value) DO UPDATE" statement raises Postgres
        // error 21000 ("cannot affect row a second time"), so the batch must be unique.
        Dictionary<(string, string), Fingerprint> unique = new();
        foreach (Fingerprint fp in fingerprints)
        {
            unique[(fp.Type, fp.Value)] = fp;
        }

        int n = unique.Count;

        // Pre-allocate arrays without LINQ to avoid enumerator overhead.
        string[] fpTypes = new string[n];
        string[] fpValues = new string[n];
        string[] deviceIds = new string[n];
        DateTimeOffset[] nowArr = new DateTimeOffset[n];
        string[] sourceArr = new string[n];
        int idx = 0;
        foreach (Fingerprint fp in unique.Values)
        {
            fpTypes[idx] = fp.Type;
            fpValues[idx] = fp.Value;
            deviceIds[idx] = deviceId;
            nowArr[idx] = now;
            sourceArr[idx] = source;
            idx++;
        }

        cmd.Parameters.Add(Param.TextArray(fpTypes));
        cmd.Parameters.Add(Param.TextArray(fpValues));
        cmd.Parameters.Add(Param.TextArray(deviceIds));
        cmd.Parameters.Add(Param.TimestampTzArray(nowArr));
        cmd.Parameters.Add(Param.TimestampTzArray(nowArr));
        cmd.Parameters.Add(Param.TextArray(sourceArr));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Manual merge ──────────────────────────────────────────────────────────

    /// <summary>
    /// Merges <paramref name="loserId" /> into <paramref name="survivorId" /> as directed by an admin.
    /// Throws <see cref="InvalidOperationException" /> if either device does not exist, they are the
    /// same device, or the loser is already aliased to a different device (caller should 409).
    /// </summary>
    public async Task ManualMergeAsync(
        string loserId,
        string survivorId,
        string actor,
        CancellationToken ct = default
    )
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        // Check alias state before checking existence: a completed merge deletes the loser's
        // devices row (see MergeLosersAsync), so a retry of the same merge must recognize
        // "already aliased to this survivor" as a no-op rather than fail loser existence.
        string? existingAlias = await GetExistingAliasAsync(conn, loserId, ct);
        if (existingAlias is not null)
        {
            if (!existingAlias.Equals(survivorId, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeviceMergeConflictException(
                    $"Device {loserId} is already aliased to {existingAlias}."
                );
            }

            return;
        }

        // Verify both devices exist (neither is an alias at this point).
        await VerifyDeviceExistsAsync(conn, survivorId, "survivor", ct);
        await VerifyDeviceExistsAsync(conn, loserId, "loser", ct);

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        await MergeLosersAsync(
            conn,
            tx,
            survivorId,
            [loserId],
            actor,
            auditAction: "device.merge",
            auditDetail: new { loser = loserId, survivor = survivorId },
            ct
        );

        await tx.CommitAsync(ct);
    }

    private static async Task VerifyDeviceExistsAsync(
        NpgsqlConnection conn,
        string deviceId,
        string role,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM devices WHERE device_id = $1::uuid LIMIT 1";
        cmd.Parameters.Add(Param.Text(deviceId));
        object? result = await cmd.ExecuteScalarAsync(ct);
        if (result is null)
        {
            throw new ArgumentException($"No device found for {role} id '{deviceId}'.", nameof(deviceId));
        }
    }

    private static async Task<string?> GetExistingAliasAsync(
        NpgsqlConnection conn,
        string deviceId,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT survivor_device_id::text FROM device_aliases WHERE alias_device_id = $1::uuid";
        cmd.Parameters.Add(Param.Text(deviceId));
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }
}

/// <summary>Thrown by ManualMergeAsync when the loser is already aliased to a different survivor.</summary>
public sealed class DeviceMergeConflictException : Exception
{
    public DeviceMergeConflictException(string message) : base(message) { }
}