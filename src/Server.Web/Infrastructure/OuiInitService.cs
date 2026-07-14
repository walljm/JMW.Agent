namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// Loads the current OUI version hash from the database into OuiUpdateService's
/// in-memory cache after migrations complete. This ensures the heartbeat endpoint
/// returns an accurate version hash from the first request.
/// </summary>
public sealed class OuiInitService : BackgroundService
{
    private readonly OuiUpdateService _oui;

    public OuiInitService(OuiUpdateService oui)
    {
        _oui = oui;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _oui.InitAsync(stoppingToken);
}