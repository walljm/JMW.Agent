using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Ingest.Context;

/// <summary>
/// The four identity context derivations (docs/plans/context-derivations.md §4). Each pairs one
/// set-based resolve query (<see cref="ContextQueries" />) with its trigger tables and output
/// path; the shared 30-second debounce bounds recompute frequency against always-send ARP/DHCP
/// batches. <c>last_seen</c> is deliberately NOT here — it changes on every resolution, so it is
/// a plain denormalized column stamped at its write sites (devices.last_seen, migration 0105),
/// not a fact.
/// </summary>
public static class ContextDerivationLibrary
{
    public static IReadOnlyList<IContextDerivation> CreateAll() =>
    [
        new HostnameContextDerivation(),
        new FriendlyNameContextDerivation(),
        new MacContextDerivation(),
        new IpContextDerivation(),
    ];

    internal static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(30);
}

public sealed class HostnameContextDerivation : IContextDerivation
{
    private static readonly IReadOnlySet<string> Tables =
        new HashSet<string>(StringComparer.Ordinal) { "proj_systems" };

    public string Name => "identity-hostname";
    public IReadOnlySet<string> TriggerTables => Tables;
    public string OutputPath => FactPaths.Derived.IdentityHostname;
    public TimeSpan MinInterval => ContextDerivationLibrary.DefaultMinInterval;

    public IAsyncEnumerable<ResolvedContextRow> ResolveAsync(NpgsqlConnection connection, CancellationToken ct) =>
        connection.ResolveIdentityHostnameAsync(ct).Select(r => new ResolvedContextRow(r.Device, r.Value));
}

public sealed class FriendlyNameContextDerivation : IContextDerivation
{
    // proj_discovered: observer-recorded names; proj_device_arp/proj_dhcp_*: batches that
    // coincide with new MAC fingerprints (which shift the newest-mac join).
    private static readonly IReadOnlySet<string> Tables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "proj_systems",
            "proj_discovered",
            "proj_device_arp",
            "proj_dhcp_leases",
            "proj_dhcp_local_leases",
        };

    public string Name => "identity-friendly-name";
    public IReadOnlySet<string> TriggerTables => Tables;
    public string OutputPath => FactPaths.Derived.IdentityFriendlyName;
    public TimeSpan MinInterval => ContextDerivationLibrary.DefaultMinInterval;

    public IAsyncEnumerable<ResolvedContextRow> ResolveAsync(NpgsqlConnection connection, CancellationToken ct) =>
        connection.ResolveIdentityFriendlyNameAsync(ct);
}

public sealed class MacContextDerivation : IContextDerivation
{
    // The MAC itself lives only in device_fingerprints (registry, never in touched-tables) —
    // these are the projections whose ingest coincides with fingerprint writes: ARP/DHCP/
    // discovery observations and interface reports.
    private static readonly IReadOnlySet<string> Tables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "proj_device_arp",
            "proj_dhcp_leases",
            "proj_dhcp_local_leases",
            "proj_discovered",
            "proj_interfaces",
        };

    public string Name => "identity-mac";
    public IReadOnlySet<string> TriggerTables => Tables;
    public string OutputPath => FactPaths.Derived.IdentityMac;
    public TimeSpan MinInterval => ContextDerivationLibrary.DefaultMinInterval;

    public IAsyncEnumerable<ResolvedContextRow> ResolveAsync(NpgsqlConnection connection, CancellationToken ct) =>
        connection.ResolveIdentityMacAsync(ct);
}

public sealed class IpContextDerivation : IContextDerivation
{
    private static readonly IReadOnlySet<string> Tables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "proj_interfaces",
            "proj_systems",
            "proj_device_arp",
            "proj_discovered",
        };

    public string Name => "identity-ip";
    public IReadOnlySet<string> TriggerTables => Tables;
    public string OutputPath => FactPaths.Derived.IdentityIp;
    public TimeSpan MinInterval => ContextDerivationLibrary.DefaultMinInterval;

    public IAsyncEnumerable<ResolvedContextRow> ResolveAsync(NpgsqlConnection connection, CancellationToken ct) =>
        connection.ResolveIdentityIpAsync(ct);
}