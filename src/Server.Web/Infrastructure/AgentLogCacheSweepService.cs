namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// Evicts stale entries from <see cref="AgentLogCache" /> on a lightweight timer (same
/// BackgroundService shape as ReleaseRescanService). Keeps the in-memory log cache from
/// accumulating pages for agents nobody is looking at anymore.
/// </summary>
public sealed partial class AgentLogCacheSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly AgentLogCache _cache;
    private readonly ILogger<AgentLogCacheSweepService> _logger;

    public AgentLogCacheSweepService(AgentLogCache cache, ILogger<AgentLogCacheSweepService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _cache.Sweep(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.SweepFailed(_logger, ex);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Agent log cache sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception ex);
    }
}