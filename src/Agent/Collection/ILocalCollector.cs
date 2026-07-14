using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Collects facts about the host the agent runs on.
/// Unlike IDeviceCollector (which connects to a remote target), a local
/// collector reads the host directly — /proc, /sys, native OS APIs, local
/// daemons. The agent calls all registered local collectors each cycle and
/// combines their output under the host's stable DeviceId.
/// Implement IsSupported to skip collectors that don't apply to the current
/// OS. The agent checks this at startup and skips unsupported collectors
/// rather than failing.
/// </summary>
public interface ILocalCollector
{
    /// <summary>Short name for logging. e.g. "hardware", "docker".</summary>
    string Name { get; }

    /// <summary>
    /// Returns false if this collector cannot run on the current OS/platform.
    /// Called once at startup. Unsupported collectors are skipped silently.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Collect facts about the local host.
    /// </summary>
    /// <param name="deviceId">Stable DeviceId for this host — use as the key dimension in all fact IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct);
}