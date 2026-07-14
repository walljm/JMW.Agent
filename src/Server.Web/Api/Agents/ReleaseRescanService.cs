namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Periodically rescans the releases directory so a binary an operator drops in
/// (or the UpdateSign tool copies in) is picked up without a server restart.
/// A no-op loop when <see cref="ReleaseManager.Enabled" /> is false (no releases
/// dir configured).
/// </summary>
public sealed partial class ReleaseRescanService : BackgroundService
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(2);

    private readonly ReleaseManager _releases;
    private readonly ILogger<ReleaseRescanService> _logger;

    public ReleaseRescanService(ReleaseManager releases, ILogger<ReleaseRescanService> logger)
    {
        _releases = releases;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_releases.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(RescanInterval);
        ScanOnce();

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ScanOnce();
        }
    }

    private void ScanOnce()
    {
        try
        {
            _releases.Scan();
        }
        catch (Exception ex)
        {
            Log.ScanFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Agent releases directory rescan failed.")]
        public static partial void ScanFailed(ILogger logger, Exception ex);
    }
}