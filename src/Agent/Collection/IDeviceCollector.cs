using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Implement this to add a new collection protocol (SSH, SNMP, NetBIOS, etc.).
/// The agent container calls CanCollect() to select the right implementation,
/// then CollectAsync() once per device per cycle. The connection is opened once
/// and kept alive for the full collection — use context.IdentifyDeviceAsync()
/// mid-session to resolve the stable DeviceId without reopening.
/// </summary>
public interface IDeviceCollector
{
    /// <summary>
    /// Collector type slug this collector handles, e.g. "ssh", "snmp", "google-wifi".
    /// Mirrors <see cref="IServiceCollector.ServiceType" /> so both collector kinds expose
    /// the same discriminator name.
    /// </summary>
    string CollectorType { get; }

    /// <summary>
    /// Fast, allocation-free check — no connection made here.
    /// Return true if this collector handles the target's collector type.
    /// </summary>
    bool CanCollect(Target target);

    /// <summary>
    /// Open the connection, identify the device, collect all data, return raw facts.
    /// Typical implementation:
    /// 1. Open connection to target.Endpoint using target.Credentials
    /// 2. Run identification commands (show version, sysDescr, etc.)
    /// 3. Build a DeviceIdentity with fingerprints + identity hints
    /// 4. Call context.RegisterProbeAsync(identity) — connection stays open
    /// 5. Run full data collection, building facts using the returned DeviceId
    /// 6. Return raw facts — normalization and derivation handled by the container
    /// Throw on unrecoverable errors (auth failure, device unreachable).
    /// The container logs and continues to the next device.
    /// </summary>
    Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    );
}