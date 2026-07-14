using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Base for local collectors that branch on host OS (review D13). Encapsulates the
/// <c>if (IsLinux) … else if (IsMacOS) … else if (IsWindows) …</c> dispatch shell — identical
/// across ~13 collectors — leaving each subclass to implement only the per-OS collection logic.
/// A subclass that doesn't support a given OS simply doesn't override that method; the default
/// no-op means <see cref="CollectAsync" /> returns an empty list on that platform.
/// </summary>
public abstract class OsDispatchLocalCollector : ILocalCollector
{
    public abstract string Name { get; }

    public virtual bool IsSupported => true;

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        CollectCommon(deviceId, facts);

        if (OperatingSystem.IsLinux())
        {
            await CollectLinuxAsync(deviceId, facts, ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await CollectMacOsAsync(deviceId, facts, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            await CollectWindowsAsync(deviceId, facts, ct);
        }

        return facts;
    }

    /// <summary>
    /// Facts emitted regardless of OS (e.g. CPU core count from <see cref="Environment" />) —
    /// runs once before the per-OS branch. Default no-op; override only if a subclass has such
    /// platform-agnostic facts, rather than repeating them in every <c>CollectXxxAsync</c> override.
    /// </summary>
    protected virtual void CollectCommon(string deviceId, List<Fact> facts)
    {
    }

    protected virtual Task CollectLinuxAsync(string deviceId, List<Fact> facts, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task CollectMacOsAsync(string deviceId, List<Fact> facts, CancellationToken ct) =>
        Task.CompletedTask;

    protected virtual Task CollectWindowsAsync(string deviceId, List<Fact> facts, CancellationToken ct) =>
        Task.CompletedTask;
}