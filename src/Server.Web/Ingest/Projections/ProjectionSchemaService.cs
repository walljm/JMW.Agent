using ITPIE.Migrations;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// After migrations complete, ensures every projection table/column declared in
/// <see cref="ProjectionLibrary" /> exists by running the idempotent additive DDL from
/// <see cref="ProjectionSchema.GenerateDdl" />. This lets a new projection column be a
/// one-line library edit instead of a hand-authored migration kept in sync by name and
/// type. Migrations still run first and own the base schema and every non-additive change
/// (renames, drops, backfills, indexes); this only fills additive gaps on top.
/// Single-instance assumption: it runs promptly after migrations, before the agent's next
/// poll. A brand-new column is queryable once this completes; a fact that races ahead of it
/// fails only its own batch and is retried next cycle (facts_history still captures it).
/// Acceptable for the self-hosted single-server deployment. A generator failure is logged
/// and swallowed — the migration-owned base schema is unaffected, so the server stays up.
/// </summary>
public sealed partial class ProjectionSchemaService : BackgroundService
{
    private readonly NpgsqlDataSource _db;
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly ILogger<ProjectionSchemaService> _logger;

    public ProjectionSchemaService(
        NpgsqlDataSource db,
        MigrationCompletedSignal migrationSignal,
        ILogger<ProjectionSchemaService> logger
    )
    {
        _db = db;
        _migrationSignal = migrationSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _migrationSignal.Completed.WaitAsync(stoppingToken);

        try
        {
            IEnumerable<ProjectionDef> defs = ProjectionLibrary.CreateAll(_db)
                .OfType<GenericProjection>()
                .Select(p => p.Def);

            await using NpgsqlConnection conn = await _db.OpenConnectionAsync(stoppingToken);
            await using NpgsqlCommand cmd = new(ProjectionSchema.GenerateDdl(defs), conn);
            await cmd.ExecuteNonQueryAsync(stoppingToken);

            Log.SchemaEnsured(_logger);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SchemaEnsureFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Projection schema ensured from ProjectionLibrary.")]
        internal static partial void SchemaEnsured(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to ensure projection schema from ProjectionLibrary; additive columns may be missing "
              + "until the next boot. The migration-owned base schema is unaffected."
        )]
        internal static partial void SchemaEnsureFailed(ILogger logger, Exception ex);
    }
}