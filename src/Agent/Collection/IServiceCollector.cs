using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Collects facts from a remote service (HTTP API, database, monitoring platform, etc.)
/// identified by a ServiceTarget.
/// Unlike IDeviceCollector (SSH/SNMP to network devices) or ILocalCollector
/// (reads from the host it runs on), a service collector connects to a structured
/// API and may collect data that belongs to a logical entity independent of any
/// single host — e.g. DNS zones, DHCP leases, VPN tunnels.
/// Collection flow:
/// 1. Connect to target.Url using target.Credentials
/// 2. Run a lightweight probe to collect identity fingerprints
/// 3. Call context.IdentifyServiceAsync(probe) — server assigns a stable ServiceId
/// 4. Collect full data set, keying all facts under DnsServer[serviceId].* or similar
/// 5. Return raw facts — normalization and derivation handled by the analysis engine
/// </summary>
public interface IServiceCollector
{
    /// <summary>
    /// Service type slug this collector handles.
    /// Must match the "collector_type" field in agent.json targets[].
    /// e.g. "technitium-dns", "adguard-home"
    /// </summary>
    string ServiceType { get; }

    /// <summary>
    /// Returns true if this collector can handle the given target.
    /// Check CollectorType and any URL/credential format requirements.
    /// </summary>
    bool CanCollect(Target target);

    /// <summary>
    /// Connect to the service, identify it, collect all data, return raw facts.
    /// </summary>
    Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        IServiceCollectionContext context,
        CancellationToken ct
    );
}

/// <summary>
/// The agent container's interface available to service collectors.
/// Resolves logical service identity without exposing HTTP transport details.
/// </summary>
public interface IServiceCollectionContext
{
    string AgentId { get; }

    /// <summary>
    /// DeviceId of the host the agent is running on.
    /// Null if the agent has no local device identity (e.g. a pure service-polling agent
    /// with no local collectors registered).
    /// Use this to emit Service[serviceId].DeviceId for cross-dimension correlation.
    /// </summary>
    string? HostDeviceId { get; }

    /// <summary>
    /// Resolves or creates a stable ServiceId for the probed service.
    /// The server matches the fingerprints against known services; if no match
    /// exists a new ServiceId is minted and stored.
    /// Call this after collecting enough data to build fingerprints (e.g. zone names)
    /// but before populating the bulk of the facts.
    /// </summary>
    Task<string> IdentifyServiceAsync(ServiceProbe probe, CancellationToken ct);
}