using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace ITPIE.Migrations;

internal sealed partial class DatabaseMigrationService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DatabaseMigrationEngine _migrationEngine;
    private readonly DatabaseMigrationScriptProvider _scriptProvider;
    private readonly DatabaseMigrationOptions _options;
    private readonly MigrationCompletedSignal _signal;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(
        NpgsqlDataSource dataSource,
        DatabaseMigrationEngine migrationEngine,
        DatabaseMigrationScriptProvider scriptProvider,
        IOptions<DatabaseMigrationOptions> options,
        MigrationCompletedSignal signal,
        ILogger<DatabaseMigrationService> logger
    )
    {
        _dataSource = dataSource;
        _migrationEngine = migrationEngine;
        _scriptProvider = scriptProvider;
        _options = options.Value;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Skip)
        {
            LogSkippingMigrations();
            _signal.SetCompleted();
            return;
        }

        if (_options.DryRun)
        {
            NotImplementedException ex = new("Dry-run is not yet implemented.");
            _signal.SetFailed(ex);
            throw ex;
        }

        try
        {
            await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(stoppingToken);

            IEnumerable<DatabaseMigrationScript> orderedMigrations = _scriptProvider.GetOrderedMigrations();

            await _migrationEngine.EnsureSchemaVersionsTableAsync(connection, stoppingToken);

            LogExecutingMigrations();

            await _migrationEngine.ExecuteOrderedMigrationsAsync(connection, orderedMigrations, stoppingToken);

            LogMigrationsComplete();
            _signal.SetCompleted();
        }
        catch (Exception ex)
        {
            _signal.SetFailed(ex);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping database migrations (Skip option enabled)")]
    private partial void LogSkippingMigrations();

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing database migrations")]
    private partial void LogExecutingMigrations();

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations complete")]
    private partial void LogMigrationsComplete();
}