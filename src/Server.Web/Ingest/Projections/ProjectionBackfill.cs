using JMW.Discovery.Core;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// One-time facts_history -> projection backfill. A projection populates only from LIVE fact
/// routing; there is no automatic replay. Combined with agent delta-tracking (unchanged facts are
/// never re-sent) and the router silently dropping facts that have no matching projection, a
/// projection added AFTER its facts already landed in history stays empty forever — how
/// proj_docker_networks shipped empty in migration 0091 despite its Device[].DockerNet[].* facts
/// existing. This reconstructs the current facts for each not-yet-backfilled projection and
/// re-routes them through the live router, exactly once per projection (tracked in the
/// <c>projection_backfills</c> watermark table). Re-routing is idempotent: the projection upsert's
/// WHERE guard no-ops rows that already match, so an already-populated projection just gets
/// watermarked. Extracted from <see cref="ProjectionSchemaService" /> so it is directly testable.
/// </summary>
public static partial class ProjectionBackfill
{
    // Route reconstructed facts in bounded batches so a first-boot backfill over the whole fact
    // set doesn't build one enormous parameter array per projection.
    private const int ChunkSize = 5_000;

    /// <summary>
    /// Backfills each projection in <paramref name="defs" /> that has no <c>projection_backfills</c>
    /// watermark. Per-projection isolation: one projection's failure neither blocks the others nor
    /// marks it done, so it retries on the next call.
    /// </summary>
    public static async Task RunAsync(
        NpgsqlDataSource db,
        ProjectionRouter router,
        FactRepository facts,
        IReadOnlyList<ProjectionDef> defs,
        ILogger logger,
        CancellationToken ct = default
    )
    {
        HashSet<string> done;
        try
        {
            done = await LoadBackfilledTablesAsync(db, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The watermark table is migration-owned; if it can't be read, skip backfill entirely
            // rather than risk re-routing every projection on every boot.
            Log.BackfillSkipped(logger, ex);
            return;
        }

        foreach (ProjectionDef def in defs)
        {
            if (done.Contains(def.TableName))
            {
                continue;
            }

            try
            {
                string[] paths = def.Columns
                    .Select(c => c.Attribute)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                IReadOnlyList<Fact> current = await facts.ReadCurrentFactsForPathsAsync(paths, ct);
                for (int offset = 0; offset < current.Count; offset += ChunkSize)
                {
                    int len = Math.Min(ChunkSize, current.Count - offset);
                    await router.RouteAsync(current.Skip(offset).Take(len), ct);
                }

                await MarkBackfilledAsync(db, def.TableName, ct);
                Log.Backfilled(logger, def.TableName, current.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not marked done — retried on the next call/boot.
                Log.BackfillFailed(logger, def.TableName, ex);
            }
        }
    }

    private static async Task<HashSet<string>> LoadBackfilledTablesAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new("SELECT table_name FROM projection_backfills", conn);
        HashSet<string> done = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            done.Add(reader.GetString(0));
        }

        return done;
    }

    private static async Task MarkBackfilledAsync(NpgsqlDataSource db, string tableName, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "INSERT INTO projection_backfills (table_name) VALUES ($1) ON CONFLICT (table_name) DO NOTHING",
            conn
        );
        cmd.Parameters.Add(new NpgsqlParameter { Value = tableName });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Backfilled projection {Table} from facts_history ({FactCount} current facts re-routed)."
        )]
        internal static partial void Backfilled(ILogger logger, string table, int factCount);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to backfill projection {Table} from facts_history; will retry on next boot."
        )]
        internal static partial void BackfillFailed(ILogger logger, string table, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Could not read projection_backfills; skipping the facts_history backfill this boot."
        )]
        internal static partial void BackfillSkipped(ILogger logger, Exception ex);
    }
}