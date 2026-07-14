using ITPIE.Migrations;

using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Auth;

/// <summary>
/// Logs a first-run notice — and the one-time setup token <c>Bootstrap.cshtml.cs</c> requires — to
/// the console when no admin account exists yet.
/// </summary>
public sealed partial class BootstrapService : BackgroundService
{
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly ILogger<BootstrapService> _logger;
    private readonly NpgsqlDataSource _db;
    private readonly BootstrapSetupToken _setupToken;

    public BootstrapService(
        NpgsqlDataSource db,
        MigrationCompletedSignal migrationSignal,
        BootstrapSetupToken setupToken,
        ILogger<BootstrapService> logger
    )
    {
        _db = db;
        _migrationSignal = migrationSignal;
        _setupToken = setupToken;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _migrationSignal.Completed.WaitAsync(stoppingToken);

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(stoppingToken);
        AdminCountResult adminCount = await conn.CountAdminsAsync(stoppingToken).FirstOrDefaultAsync(stoppingToken);

        if (adminCount.Count is null or 0)
        {
            Console.WriteLine(
                "[JMW DISCOVERY] No admin account found. Open the web UI to complete setup at "
              + $"/bootstrap — setup token: {_setupToken.Value}"
            );
            Log.FirstRunNotice(_logger, _setupToken.Value);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[JMW DISCOVERY] First-run setup required — visit /bootstrap and enter setup token: {SetupToken}"
        )]
        internal static partial void FirstRunNotice(ILogger logger, string setupToken);
    }
}