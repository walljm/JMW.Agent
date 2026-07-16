using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Npgsql;

namespace JMW.Discovery.Server.Admin;

/// <summary>
/// Admin API for listing and resolving fingerprint conflicts — device pairs that share
/// a fingerprint without having been explicitly merged or excluded.
/// </summary>
public static class ConflictsApi
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/conflicts", ListConflicts)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/conflicts/{fp_type}/{fp_value}/resolve", ResolveConflict)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    // ── GET /admin/conflicts ──────────────────────────────────────────────────

    private static async Task<IResult> ListConflicts(
        NpgsqlDataSource db,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        string? afterFpType = null;
        string? afterFpValue = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecodeParts(after, 2, out string[] parts))
            {
                return ApiError.InvalidCursor();
            }

            afterFpType = parts[0];
            afterFpValue = parts[1];
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT df.fp_type, df.fp_value,
                   array_agg(DISTINCT df.device_id::text ORDER BY df.device_id::text) AS device_ids
            FROM device_fingerprints df
            WHERE NOT EXISTS (
                SELECT 1 FROM excluded_fingerprints ef
                WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
            )
            AND ($1::text IS NULL OR (df.fp_type, df.fp_value) > ($1::text, $2::text))
            GROUP BY df.fp_type, df.fp_value
            HAVING COUNT(DISTINCT df.device_id) > 1
            ORDER BY df.fp_type ASC, df.fp_value ASC
            LIMIT $3
            """;
        cmd.Parameters.Add(Param.Text(afterFpType));
        cmd.Parameters.Add(Param.Text(afterFpValue));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<ConflictRow> rows = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(
                new ConflictRow(
                    FpType: reader.GetString(0),
                    FpValue: reader.GetString(1),
                    DeviceIds: (string[])reader.GetValue(2)
                )
            );
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            ConflictRow last = rows[^1];
            nextCursor = KeysetCursor.EncodeParts(last.FpType, last.FpValue);
        }

        return Results.Ok(
            new
            {
                items = rows,
                next_cursor = nextCursor,
            }
        );
    }

    // ── POST /admin/conflicts/{fp_type}/{fp_value}/resolve ────────────────────

    private static async Task<IResult> ResolveConflict(
        string fp_type,
        string fp_value,
        ResolveConflictRequest body,
        DeviceRegistry registry,
        NpgsqlDataSource db,
        HttpContext context,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(fp_type) || string.IsNullOrWhiteSpace(fp_value))
        {
            return ApiError.InvalidRequest("fp_type and fp_value are required.");
        }

        string actor = context.User.Identity?.Name ?? "unknown";

        if (body.Action == "exclude")
        {
            return await ExcludeFingerprintAsync(fp_type, fp_value, actor, db, ct);
        }

        if (body.Action == "merge")
        {
            if (!Guid.TryParse(body.WinnerDeviceId, out _))
            {
                return ApiError.InvalidId("winner_device_id must be a valid GUID for merge action.");
            }

            return await MergeConflictAsync(fp_type, fp_value, body.WinnerDeviceId!, actor, registry, db, ct);
        }

        return ApiError.InvalidRequest("action must be 'merge' or 'exclude'.");
    }

    private static async Task<IResult> ExcludeFingerprintAsync(
        string fpType,
        string fpValue,
        string actor,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        await using (NpgsqlCommand insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO excluded_fingerprints (fp_type, fp_value)
                VALUES ($1, $2)
                ON CONFLICT DO NOTHING
                """;
            insertCmd.Parameters.Add(Param.Text(fpType));
            insertCmd.Parameters.Add(Param.Text(fpValue));
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await AuditLog.WriteAsync(
            conn,
            tx,
            actor,
            "fingerprint.exclude",
            $"{fpType}:{fpValue}",
            new { fp_type = fpType, fp_value = fpValue },
            ct
        );

        await tx.CommitAsync(ct);

        // Resolve immediately rather than waiting for FingerprintConflictSweepService's next tick.
        await conn.ResolveIncidentManualAsync("fingerprint", $"{fpType}:{fpValue}", "fingerprint_conflict", ct)
            .ExecuteAsync(ct);

        return Results.Ok(
            new
            {
                action = "excluded",
                fp_type = fpType,
                fp_value = fpValue,
            }
        );
    }

    private static async Task<IResult> MergeConflictAsync(
        string fpType,
        string fpValue,
        string winnerDeviceId,
        string actor,
        DeviceRegistry registry,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        // Find all device_ids that share this fingerprint (excluding already-excluded entries).
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand findCmd = conn.CreateCommand();
        findCmd.CommandText = """
            SELECT DISTINCT device_id::text
            FROM device_fingerprints
            WHERE fp_type = $1 AND fp_value = $2
              AND NOT EXISTS (
                  SELECT 1 FROM excluded_fingerprints ef
                  WHERE ef.fp_type = fp_type AND ef.fp_value = fp_value
              )
            """;
        findCmd.Parameters.Add(Param.Text(fpType));
        findCmd.Parameters.Add(Param.Text(fpValue));

        List<string> deviceIds = [];
        await using (NpgsqlDataReader reader = await findCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                deviceIds.Add(reader.GetString(0));
            }
        }

        if (!deviceIds.Contains(winnerDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            return ApiError.InvalidRequest("winner_device_id is not one of the conflicting devices.");
        }

        List<string> losers = deviceIds
            .Where(id => !id.Equals(winnerDeviceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string loser in losers)
        {
            try
            {
                // ManualMergeAsync records its own "merged" change_event after committing, so
                // no separate write is needed here.
                await registry.ManualMergeAsync(loserId: loser, survivorId: winnerDeviceId, actor: actor, ct: ct);
            }
            catch (DeviceMergeConflictException ex)
            {
                return ApiError.Conflict(ex.Message);
            }
        }

        // Resolve immediately rather than waiting for FingerprintConflictSweepService's next tick.
        await conn.ResolveIncidentManualAsync("fingerprint", $"{fpType}:{fpValue}", "fingerprint_conflict", ct)
            .ExecuteAsync(ct);

        return Results.Ok(
            new
            {
                action = "merged",
                winner_device_id = winnerDeviceId,
                merged_count = losers.Count,
            }
        );
    }
}

public sealed record ConflictRow(string FpType, string FpValue, string[] DeviceIds);

public sealed record ResolveConflictRequest(string Action, string? WinnerDeviceId);