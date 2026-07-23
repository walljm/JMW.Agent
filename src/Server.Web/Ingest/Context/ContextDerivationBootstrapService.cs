using ITPIE.Migrations;

namespace JMW.Discovery.Server.Ingest.Context;

/// <summary>
/// Runs one unconditional context-derivation pass at startup (docs/plans/context-derivations.md
/// §5): populates proj_devices' identity columns from current state (bootstrap/backfill —
/// recompute-all is idempotent, so no watermark is needed, unlike ProjectionBackfill) and
/// doubles as the engine's cache warm-up. Waits for migrations (the identity columns are
/// migration-created, 0106). A failure is logged and swallowed — the ingest-gated passes
/// self-heal on the next batch.
/// </summary>
public sealed partial class ContextDerivationBootstrapService : BackgroundService
{
    private readonly ContextDerivationEngine _engine;
    private readonly MigrationCompletedSignal _migrationSignal;
    private readonly ILogger<ContextDerivationBootstrapService> _logger;

    public ContextDerivationBootstrapService(
        ContextDerivationEngine engine,
        MigrationCompletedSignal migrationSignal,
        ILogger<ContextDerivationBootstrapService> logger
    )
    {
        _engine = engine;
        _migrationSignal = migrationSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _migrationSignal.Completed.WaitAsync(stoppingToken);

        try
        {
            await _engine.RunAllAsync(stoppingToken);
            Log.Bootstrapped(_logger);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BootstrapFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Context derivations bootstrapped from current state.")]
        internal static partial void Bootstrapped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Context-derivation bootstrap failed; identity values self-heal on ingest-gated passes."
        )]
        internal static partial void BootstrapFailed(ILogger logger, Exception ex);
    }
}