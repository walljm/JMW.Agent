using System.Diagnostics.CodeAnalysis;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Projections;
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
/// The whole resolve (match lookup + whichever branch runs) is one transaction, serialized
/// against every other resolve/merge/delete via <see cref="ResolveLockKey"/> — see
/// <see cref="AcquireResolveLockAsync"/>.
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

        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        await AcquireResolveLockAsync(conn, tx, ct);

        // Collect all device_ids that match AND are not excluded.
        List<string> matchedIds = await FindMatchingDeviceIdsAsync(conn, tx, normalized, ct);
        bool wasMerge = matchedIds.Count > 1;

        (string DeviceId, bool IsNew) result = matchedIds.Count switch
        {
            0 => await CreateDeviceAsync(conn, tx, normalized, source, managementStatus, ct),
            1 => await UpdateFingerprintsAsync(conn, tx, matchedIds[0], normalized, source, managementStatus, isNew: false, ct),
            _ => await AutoMergeAsync(conn, tx, matchedIds, normalized, source, managementStatus, ct),
        };

        await tx.CommitAsync(ct);

        // Every branch above has committed by now, so these writes are safely outside the
        // transaction — matches the existing convention for one-shot change-event recording.
        if (result.IsNew)
        {
            await conn.InsertChangeEventAsync("discovered", "device", result.DeviceId, detail: null, ct)
                .ExecuteAsync(ct);
        }
        else if (wasMerge)
        {
            List<string> losers = matchedIds.Where(id => id != result.DeviceId).ToList();
            await RecordMergedChangeEventAsync(conn, result.DeviceId, losers, ct);
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

    // ── Cross-request locking ───────────────────────────────────────────────────

    // Fixed key scoping ALL device resolution (auto-merge at ingest) plus manual merge/delete
    // admin actions to one at a time fleet-wide.
    private const long ResolveLockKey = 0x4A4D575F444B4C4B; // arbitrary but fixed ("JMW_DKLK")

    /// <summary>
    /// Serializes the whole resolve/merge/delete critical section against every other one.
    /// Closes a TOCTOU race: FindMatchingDeviceIdsAsync reads which devices share a fingerprint
    /// with its own reader, and only after that reader closes do writes begin — two concurrent
    /// resolves with overlapping new fingerprints could otherwise each see the same matches and
    /// independently decide on (possibly different) survivors. Transaction-scoped, so it releases
    /// automatically on commit or rollback — no manual unlock bookkeeping needed. Full
    /// serialization (rather than partial/fingerprint-scoped locking) is deliberate: this is a
    /// single-operator, small-to-medium fleet (ADR-002), so serializing this fast, rarely-conflicting
    /// operation costs nothing in practice.
    /// </summary>
    private static async Task AcquireResolveLockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT pg_advisory_xact_lock($1)";
        cmd.Parameters.Add(Param.Bigint(ResolveLockKey));
        await cmd.ExecuteScalarAsync(ct);
    }

    private static Task RecordMergedChangeEventAsync(
        NpgsqlConnection conn,
        string survivor,
        IReadOnlyList<string> losers,
        CancellationToken ct
    ) =>
        conn.InsertChangeEventAsync("merged", "device", survivor, $"absorbed {string.Join(",", losers)}", ct)
            .ExecuteAsync(ct);

    // ── DB operations ─────────────────────────────────────────────────────────

    private static async Task<List<string>> FindMatchingDeviceIdsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<Fingerprint> fingerprints,
        CancellationToken ct
    )
    {
        // Skip any (fp_type, fp_value) pairs in excluded_fingerprints.
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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

        // Reader closed (disposed) before returning — caller may issue writes on the same connection.
        return ids;
    }

    private static async Task<(string DeviceId, bool IsNew)> CreateDeviceAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        CancellationToken ct
    )
    {
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

        return (deviceId, IsNew: true);
    }

    private static async Task<(string DeviceId, bool IsNew)> UpdateFingerprintsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string deviceId,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        bool isNew,
        CancellationToken ct
    )
    {
        await UpsertFingerprintsAsync(conn, tx, deviceId, fingerprints, source, ct);
        await PromoteToManagedAsync(conn, tx, deviceId, managementStatus, ct);
        return (deviceId, isNew);
    }

    private static async Task<(string DeviceId, bool IsNew)> AutoMergeAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<string> deviceIds,
        List<Fingerprint> fingerprints,
        string source,
        string managementStatus,
        CancellationToken ct
    )
    {
        string survivor = await FindOldestDeviceIdAsync(conn, tx, deviceIds, ct);
        List<string> losers = deviceIds.Where(id => id != survivor).ToList();

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
        NpgsqlTransaction tx,
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
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE devices SET management_status = 'managed', updated_at = now()
            WHERE device_id = $1::uuid AND management_status = 'discovered'
            """;
        cmd.Parameters.Add(Param.Text(deviceId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Aliases every loser to survivor, reassigns their fingerprints, appends them to
    /// survivor's merged_from (idempotent — losers already recorded are skipped, so retrying
    /// the same merge is safe), repoints every other loser-owned row (projections, agents'
    /// device link, change_events, incidents) onto the survivor per ADR-002/COMP-004, and
    /// deletes the losers' own devices rows so nothing of them is independently browsable
    /// afterward. Shared by <see cref="AutoMergeAsync"/> (N losers from fingerprint collision)
    /// and <see cref="ManualMergeAsync"/> (1 loser from an admin action).
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

        // change_events is one-shot history like facts_history — same reasoning, no uniqueness
        // constraint, so a single batched repoint is safe.
        await using (NpgsqlCommand changeEventsCmd = conn.CreateCommand())
        {
            changeEventsCmd.Transaction = tx;
            changeEventsCmd.CommandText = """
                UPDATE change_events SET entity_id = $2
                WHERE entity_kind = 'device' AND entity_id = ANY($1::text[])
                """;
            changeEventsCmd.Parameters.Add(Param.TextArray(losers));
            changeEventsCmd.Parameters.Add(Param.Text(survivor));
            await changeEventsCmd.ExecuteNonQueryAsync(ct);
        }

        // agents.device_id is the only real FK into devices (ON DELETE SET NULL) — without this,
        // the DELETE FROM devices below would silently sever an agent's link to its own host
        // device. Plain attribute column, not part of any key, so no collision is possible.
        await using (NpgsqlCommand agentsCmd = conn.CreateCommand())
        {
            agentsCmd.Transaction = tx;
            agentsCmd.CommandText = "UPDATE agents SET device_id = $2::uuid WHERE device_id = ANY($1::uuid[])";
            agentsCmd.Parameters.Add(Param.TextArray(losers));
            agentsCmd.Parameters.Add(Param.Text(survivor));
            await agentsCmd.ExecuteNonQueryAsync(ct);
        }

        await RepointIncidentsAsync(conn, tx, survivor, losers, ct);

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

        // Repoint each loser's projection rows onto the survivor instead of discarding them —
        // otherwise a device's current-state tabs (interfaces, hardware, disks, ...) go blank
        // the instant it's merged and stay blank until the next poll happens to touch every
        // one of those fact sources again.
        await RepointProjectionsAsync(conn, tx, survivor, losers, ct);

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

    /// <summary>
    /// Repoints incidents.entity_id from each loser to the survivor. Closed incidents
    /// (resolved_at IS NOT NULL) never collide — the uniqueness constraint is partial, scoped to
    /// open rows only — so those repoint in one batched statement. Open incidents can collide:
    /// incidents_open_uq allows at most one open row per (entity, incident_type), and it's
    /// plausible for both the loser and survivor to have independently opened the same incident
    /// type (e.g. both flagged agent_offline) before the merge. On a collision, the loser's open
    /// incident is resolved as superseded (resolution='merged') rather than deleted, preserving
    /// its audit trail; IncidentEvaluator's next tick reopens a fresh one under the survivor if
    /// the underlying condition is still true. Processed one loser at a time — like the
    /// projection repoint — so a second loser's collision is judged against the first loser's
    /// already-settled row instead of racing it in the same statement.
    /// </summary>
    private static async Task RepointIncidentsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string survivor,
        string[] losers,
        CancellationToken ct
    )
    {
        await using (NpgsqlCommand closedCmd = conn.CreateCommand())
        {
            closedCmd.Transaction = tx;
            closedCmd.CommandText = """
                UPDATE incidents SET entity_id = $2
                WHERE entity_kind = 'device' AND entity_id = ANY($1::text[]) AND resolved_at IS NOT NULL
                """;
            closedCmd.Parameters.Add(Param.TextArray(losers));
            closedCmd.Parameters.Add(Param.Text(survivor));
            await closedCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (string loser in losers)
        {
            // Non-colliding open incidents move over outright.
            await using (NpgsqlCommand moveCmd = conn.CreateCommand())
            {
                moveCmd.Transaction = tx;
                moveCmd.CommandText = """
                    UPDATE incidents
                    SET entity_id = $2
                    WHERE entity_kind = 'device' AND entity_id = $1 AND resolved_at IS NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM incidents s
                          WHERE s.entity_kind = 'device' AND s.entity_id = $2 AND s.resolved_at IS NULL
                            AND s.incident_type = incidents.incident_type
                      )
                    """;
                moveCmd.Parameters.Add(Param.Text(loser));
                moveCmd.Parameters.Add(Param.Text(survivor));
                await moveCmd.ExecuteNonQueryAsync(ct);
            }

            // Whatever's still open under this loser must be a collision (the non-colliding
            // ones were just moved) — resolve as superseded rather than deleting, then repoint.
            await using (NpgsqlCommand resolveCmd = conn.CreateCommand())
            {
                resolveCmd.Transaction = tx;
                resolveCmd.CommandText = """
                    UPDATE incidents
                    SET entity_id = $2, resolved_at = now(), resolution = 'merged', last_seen_at = now()
                    WHERE entity_kind = 'device' AND entity_id = $1 AND resolved_at IS NULL
                    """;
                resolveCmd.Parameters.Add(Param.Text(loser));
                resolveCmd.Parameters.Add(Param.Text(survivor));
                await resolveCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>
    /// Repoints every device-scoped projection table onto the survivor. Reads
    /// <see cref="ProjectionLibrary.AllDefs"/> directly rather than a hand-maintained table
    /// list, so a projection added there is automatically covered here too — this is what
    /// closed the gap where proj_device_certs and proj_discovered_tls were silently skipped
    /// by the old hardcoded list.
    /// </summary>
    private static async Task RepointProjectionsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string survivor,
        string[] losers,
        CancellationToken ct
    )
    {
        foreach (ProjectionDef def in ProjectionLibrary.AllDefs)
        {
            if (def.DimensionNames.Count > 0 && def.DimensionNames[0] == "Device")
            {
                await RepointDeviceDimensionedProjectionAsync(conn, tx, def, survivor, losers, ct);
                continue;
            }

            // Not device-dimensioned (e.g. Service-keyed) — but some of these carry a plain
            // device_id backreference column (proj_services) that still needs repointing even
            // though the row's own identity (the service) never collides across a merge.
            ProjectionColumnDef? deviceIdColumn = def.Columns.FirstOrDefault(c => c.ColumnName == "device_id");
            if (deviceIdColumn is null)
            {
                continue;
            }

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"UPDATE {def.TableName} SET {deviceIdColumn.ColumnName} = $2 " +
                $"WHERE {deviceIdColumn.ColumnName} = ANY($1::text[])";
            cmd.Parameters.Add(Param.TextArray(losers));
            cmd.Parameters.Add(Param.Text(survivor));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Repoints one device-dimensioned projection table (device is part of the row's primary
    /// key — either the whole key, e.g. proj_hardware, or the first column of a composite key,
    /// e.g. proj_interfaces keyed on (device, interface)):
    /// 1. Rows whose secondary key (if any) doesn't already exist under the survivor move over
    ///    outright — this is the common case (e.g. the survivor never had its own hardware row).
    /// 2. Rows that collide with an existing survivor row (same secondary key on both sides,
    ///    which happens when the same physical device was independently tracked under both
    ///    identities) are resolved by freshness: the loser's data overwrites the survivor's row
    ///    only when the loser was confirmed more recently (updated_at). Column-level splicing
    ///    isn't attempted — mixing columns from two independently-aged snapshots has no
    ///    principled tie-break, so the whole row from the fresher side wins.
    /// 3. Whatever remains under a loser id afterward (moved, or lost the freshness compare) is
    ///    dropped.
    /// </summary>
    private static async Task RepointDeviceDimensionedProjectionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ProjectionDef def,
        string survivor,
        string[] losers,
        CancellationToken ct
    )
    {
        string table = def.TableName;
        string[] dimCols = def.DimensionNames.Select(n => n.ToLowerInvariant()).ToArray();
        string deviceCol = dimCols[0];
        string[] secondaryCols = dimCols[1..];
        string[] dataCols = def.Columns.Select(c => c.ColumnName).ToArray();

        string existsSecondaryMatch = secondaryCols.Length == 0
            ? ""
            : " AND " + string.Join(" AND ", secondaryCols.Select(c => $"s.{c} = {table}.{c}"));
        string joinSecondaryMatch = secondaryCols.Length == 0
            ? ""
            : " AND " + string.Join(" AND ", secondaryCols.Select(c => $"tgt.{c} = src.{c}"));
        string setClause = dataCols.Length == 0
            ? ""
            : string.Join(", ", dataCols.Select(c => $"{c} = src.{c}"));

        // One loser at a time, not batched: a second loser's row must see the first loser's
        // row already settled onto the survivor, otherwise two different losers both claiming
        // the same (survivor, secondary-key) pair in one UPDATE would hit a primary-key
        // violation (e.g. a 3-way auto-merge where two losers both have an "eth0" row).
        foreach (string loser in losers)
        {
            await using (NpgsqlCommand moveCmd = conn.CreateCommand())
            {
                moveCmd.Transaction = tx;
                moveCmd.CommandText = $"""
                    UPDATE {table}
                    SET {deviceCol} = $2
                    WHERE {deviceCol} = $1
                      AND NOT EXISTS (
                          SELECT 1 FROM {table} s
                          WHERE s.{deviceCol} = $2{existsSecondaryMatch}
                      )
                    """;
                moveCmd.Parameters.Add(Param.Text(loser));
                moveCmd.Parameters.Add(Param.Text(survivor));
                await moveCmd.ExecuteNonQueryAsync(ct);
            }

            if (dataCols.Length > 0)
            {
                await using NpgsqlCommand overwriteCmd = conn.CreateCommand();
                overwriteCmd.Transaction = tx;
                overwriteCmd.CommandText = $"""
                    UPDATE {table} AS tgt
                    SET {setClause}, updated_at = src.updated_at
                    FROM {table} AS src
                    WHERE tgt.{deviceCol} = $2
                      AND src.{deviceCol} = $1
                      {joinSecondaryMatch}
                      AND src.updated_at > tgt.updated_at
                    """;
                overwriteCmd.Parameters.Add(Param.Text(loser));
                overwriteCmd.Parameters.Add(Param.Text(survivor));
                await overwriteCmd.ExecuteNonQueryAsync(ct);
            }

            await using (NpgsqlCommand deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $"DELETE FROM {table} WHERE {deviceCol} = $1";
                deleteCmd.Parameters.Add(Param.Text(loser));
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async Task<string> FindOldestDeviceIdAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<string> deviceIds,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
        NpgsqlTransaction tx,
        string deviceId,
        List<Fingerprint> fingerprints,
        string source,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;

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
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        await AcquireResolveLockAsync(conn, tx, ct);

        // Check alias state before checking existence: a completed merge deletes the loser's
        // devices row (see MergeLosersAsync), so a retry of the same merge must recognize
        // "already aliased to this survivor" as a no-op rather than fail loser existence.
        string? existingAlias = await GetExistingAliasAsync(conn, tx, loserId, ct);
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
        await VerifyDeviceExistsAsync(conn, tx, survivorId, "survivor", ct);
        await VerifyDeviceExistsAsync(conn, tx, loserId, "loser", ct);

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

        await RecordMergedChangeEventAsync(conn, survivorId, [loserId], ct);
    }

    private static async Task VerifyDeviceExistsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string deviceId,
        string role,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
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
        NpgsqlTransaction tx,
        string deviceId,
        CancellationToken ct
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT survivor_device_id::text FROM device_aliases WHERE alias_device_id = $1::uuid";
        cmd.Parameters.Add(Param.Text(deviceId));
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is string s ? s : null;
    }

    // ── Manual delete (fallback for a bad merge with no usable re-split) ───────

    /// <summary>
    /// Hard-deletes a device and every row associated with it: fingerprints, projections,
    /// fact/metrics history, change events, incidents, and (if it's currently a survivor) the
    /// aliases pointing at it. Does NOT attempt to reconstruct or restore anything — this is a
    /// deliberately blunt fallback for a bad auto-merge where a true re-split isn't reconstructable
    /// (the merge hard-deletes the loser's original devices row, and fingerprint provenance isn't
    /// logged anywhere once reassigned). After deletion, the next time this physical entity is
    /// observed it resolves as a brand-new device rather than joining stale state. If the same
    /// fingerprint overlap caused the bad merge, it will recur on rediscovery unless paired with
    /// excluding the offending fingerprint (existing excluded_fingerprints mechanism).
    /// </summary>
    public async Task DeleteDeviceAsync(string deviceId, string actor, CancellationToken ct = default)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);
        await AcquireResolveLockAsync(conn, tx, ct);

        await VerifyDeviceExistsAsync(conn, tx, deviceId, "device", ct);

        // Un-alias any devices previously merged into this one — otherwise their alias would
        // point at a survivor that no longer exists. (deviceId itself can't currently be an
        // alias_device_id: VerifyDeviceExistsAsync above guarantees it still has a live devices
        // row, and a merge always deletes the loser's row in the same transaction as aliasing it.)
        await using (NpgsqlCommand unaliasCmd = conn.CreateCommand())
        {
            unaliasCmd.Transaction = tx;
            unaliasCmd.CommandText = "DELETE FROM device_aliases WHERE survivor_device_id = $1::uuid";
            unaliasCmd.Parameters.Add(Param.Text(deviceId));
            await unaliasCmd.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlCommand fpCmd = conn.CreateCommand())
        {
            fpCmd.Transaction = tx;
            fpCmd.CommandText = "DELETE FROM device_fingerprints WHERE device_id = $1::uuid";
            fpCmd.Parameters.Add(Param.Text(deviceId));
            await fpCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (string table in new[] { "facts_history", "metrics_raw" })
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE key_values ->> 'Device' = $1";
            cmd.Parameters.Add(Param.Text(deviceId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlCommand changeEventsCmd = conn.CreateCommand())
        {
            changeEventsCmd.Transaction = tx;
            changeEventsCmd.CommandText = "DELETE FROM change_events WHERE entity_kind = 'device' AND entity_id = $1";
            changeEventsCmd.Parameters.Add(Param.Text(deviceId));
            await changeEventsCmd.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlCommand incidentsCmd = conn.CreateCommand())
        {
            incidentsCmd.Transaction = tx;
            incidentsCmd.CommandText = "DELETE FROM incidents WHERE entity_kind = 'device' AND entity_id = $1";
            incidentsCmd.Parameters.Add(Param.Text(deviceId));
            await incidentsCmd.ExecuteNonQueryAsync(ct);
        }

        await DeleteProjectionsForDeviceAsync(conn, tx, deviceId, ct);

        // agents.device_id (the only real FK into devices) is ON DELETE SET NULL — handled
        // automatically by the devices DELETE below.
        await using (NpgsqlCommand deleteDeviceCmd = conn.CreateCommand())
        {
            deleteDeviceCmd.Transaction = tx;
            deleteDeviceCmd.CommandText = "DELETE FROM devices WHERE device_id = $1::uuid";
            deleteDeviceCmd.Parameters.Add(Param.Text(deviceId));
            await deleteDeviceCmd.ExecuteNonQueryAsync(ct);
        }

        await AuditLog.WriteAsync(conn, tx, actor, "device.delete", deviceId, detail: null, ct);

        await tx.CommitAsync(ct);
    }

    private static async Task DeleteProjectionsForDeviceAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string deviceId,
        CancellationToken ct
    )
    {
        foreach (ProjectionDef def in ProjectionLibrary.AllDefs)
        {
            if (def.DimensionNames.Count > 0 && def.DimensionNames[0] == "Device")
            {
                string deviceCol = def.DimensionNames[0].ToLowerInvariant();
                await using NpgsqlCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {def.TableName} WHERE {deviceCol} = $1";
                cmd.Parameters.Add(Param.Text(deviceId));
                await cmd.ExecuteNonQueryAsync(ct);
                continue;
            }

            ProjectionColumnDef? deviceIdColumn = def.Columns.FirstOrDefault(c => c.ColumnName == "device_id");
            if (deviceIdColumn is null)
            {
                continue;
            }

            await using NpgsqlCommand nullCmd = conn.CreateCommand();
            nullCmd.Transaction = tx;
            nullCmd.CommandText =
                $"UPDATE {def.TableName} SET {deviceIdColumn.ColumnName} = NULL " +
                $"WHERE {deviceIdColumn.ColumnName} = $1";
            nullCmd.Parameters.Add(Param.Text(deviceId));
            await nullCmd.ExecuteNonQueryAsync(ct);
        }
    }
}

/// <summary>Thrown by ManualMergeAsync when the loser is already aliased to a different survivor.</summary>
public sealed class DeviceMergeConflictException : Exception
{
    public DeviceMergeConflictException(string message) : base(message) { }
}
