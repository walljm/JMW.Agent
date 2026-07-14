using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Passed into IDeviceCollector.CollectAsync so the collector can interact
/// with the agent container without managing transport details.
/// The collector calls RegisterProbeAsync() once it has fingerprints, receives
/// a stable placeholder device ID to use in fact IDs, and then the agent
/// container assembles the fingerprints + facts into a FactBatchElement for
/// the server to resolve.
/// </summary>
public interface ICollectionContext
{
    /// <summary>Stable UUID identifying this agent instance.</summary>
    string AgentId { get; }

    /// <summary>
    /// The fingerprints registered via RegisterProbeAsync. Null until the
    /// collector calls RegisterProbeAsync. The agent reads this after collection
    /// to assemble the FactBatchElement.
    /// </summary>
    IReadOnlyList<Fingerprint>? ResolvedFingerprints { get; }

    /// <summary>
    /// The identity registered via RegisterProbeAsync. Null until the collector calls
    /// RegisterProbeAsync. The agent uses Kind/Vendor/OsFamily/OsVersion to auto-emit the
    /// corresponding raw fact after collection when the collector didn't already emit one
    /// explicitly for this device (see Agent.CollectDeviceAsync) — without this, a collector
    /// that only sets these on DeviceIdentity (rather than also adding an explicit fact) would
    /// have that data silently discarded; nothing else ever reads DeviceIdentity fields past
    /// this point.
    /// </summary>
    DeviceIdentity? ResolvedIdentity { get; }

    /// <summary>
    /// Register the device probe and receive a stable placeholder ID to use
    /// as the Device key in fact IDs (e.g. "Device[{placeholder}].Interface[eth0].Speed").
    /// The placeholder is a fingerprint-derived key that is consistent across
    /// runs for the same physical device. The server replaces it with the
    /// real device_id after resolving the fingerprints.
    /// Call this as soon as the collector has enough information to identify
    /// the device (typically right after "show version" or equivalent).
    /// </summary>
    Task<string> RegisterProbeAsync(DeviceIdentity identity, CancellationToken ct);
}