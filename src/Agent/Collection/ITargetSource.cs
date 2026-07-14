namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Provides the set of targets to collect from each cycle.
/// The config-file implementation returns a static list. Future implementations
/// may pull from the server, discover via mDNS/ARP, or read a dynamic inventory.
/// </summary>
public interface ITargetSource
{
    /// <summary>
    /// Returns the current targets for this collection cycle.
    /// Called once per interval. May return a different set each time.
    /// </summary>
    Task<IReadOnlyList<Target>> GetTargetsAsync(CancellationToken ct);
}

/// <summary>
/// Returns a fixed list of targets loaded from the agent config file.
/// </summary>
internal sealed class ConfigTargetSource : ITargetSource
{
    private readonly IReadOnlyList<Target> _targets;

    public ConfigTargetSource(IReadOnlyList<Target> targets)
    {
        _targets = targets;
    }

    public Task<IReadOnlyList<Target>> GetTargetsAsync(CancellationToken ct) =>
        Task.FromResult(_targets);
}