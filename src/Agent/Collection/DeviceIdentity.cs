using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// What a collector knows about a device after the identification phase.
/// Returned from the collector mid-session so the agent can resolve the
/// stable DeviceId without closing the connection.
/// </summary>
public sealed record DeviceIdentity(
    IReadOnlyList<Fingerprint> Fingerprints,
    string? Kind, // "router", "switch", "firewall", "vm"
    string? Vendor, // "cisco", "arista", "juniper" — normalized slug
    string? OsFamily, // "ios-xe", "eos", "junos", "linux"
    string? OsVersion
);