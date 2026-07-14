using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ITPIE.Migrations;

internal sealed partial class DatabaseMigrationEngine
{
    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowMigrationError(DatabaseMigrationScript migration, Exception innerException)
        => throw new DatabaseMigrationException(
            $"An error occurred while executing migration script: '{migration.ScriptName}'.",
            innerException
        );

    private static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<DatabaseMigrationEngine> _logger;

    public DatabaseMigrationEngine(ILogger<DatabaseMigrationEngine> logger)
    {
        _logger = logger;
    }

    public async Task EnsureSchemaVersionsTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await DatabaseMigrationQueries.SchemaVersionsTableExistsAsync(connection, cancellationToken))
        {
            await DatabaseMigrationQueries.CreateSchemaVersionsTableAsync(connection, cancellationToken);
            LogCreatedSchemaVersionsTable();
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ExecuteOrderedMigrationsAsync(
        NpgsqlConnection connection,
        IEnumerable<DatabaseMigrationScript> migrations,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        Dictionary<string, SchemaVersionsRow> versionsLookup = await DatabaseMigrationQueries
            .GetSchemaVersionsAsync(connection, cancellationToken)
            .ToDictionaryAsync(v => v.ScriptName, cancellationToken: cancellationToken);

        SchemaVersionsRow? previousVersion =
            await DatabaseMigrationQueries.GetCurrentSchemaVersionAsync(connection, cancellationToken);

        LogSchemaVersion(previousVersion?.ScriptName ?? "(none)", previousVersion?.SchemaVersionsId ?? 0);

        SchemaVersionsRow? nextVersion = null;

        foreach (DatabaseMigrationScript migration in migrations)
        {
            if (versionsLookup.TryGetValue(migration.ScriptName, out SchemaVersionsRow? appliedVersion))
            {
                LogSkippedMigration(appliedVersion.ScriptName);
                continue;
            }

            string commandText = await migration.GetCommandTextAsync(cancellationToken);

            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                await DatabaseMigrationQueries.ExecuteMigrationAsync(
                    connection,
                    commandText,
                    MigrationCommandTimeout,
                    cancellationToken
                );
            }
            catch (NpgsqlException e)
            {
                ThrowMigrationError(migration, e);
            }

            stopwatch.Stop();

            nextVersion = await DatabaseMigrationQueries.WriteSchemaVersionAsync(
                connection,
                migration.ScriptName,
                cancellationToken
            );

            LogExecutedMigration(nextVersion?.ScriptName ?? migration.ScriptName, stopwatch.Elapsed);
        }

        if (nextVersion is not null)
        {
            LogSchemaVersion(nextVersion.ScriptName, nextVersion.SchemaVersionsId);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created schemaversions table")]
    private partial void LogCreatedSchemaVersionsTable();

    [LoggerMessage(Level = LogLevel.Information, Message = "Current schema version: {ScriptName} (id: {VersionId})")]
    private partial void LogSchemaVersion(string scriptName, int versionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping already applied migration: {ScriptName}")]
    private partial void LogSkippedMigration(string scriptName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Executed migration {ScriptName} in {Elapsed}")]
    private partial void LogExecutedMigration(string scriptName, TimeSpan elapsed);
}