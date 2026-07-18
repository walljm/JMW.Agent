using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class ServiceQueries
{
    // ── Reporting: Services ─────────────────────────────────────────────────────

    /// <summary>
    /// Lists logical services with CA status/expiry and DNS query stats, keyset paginated by
    /// service ID ascending. Supports an exact type filter and a free-text search over service
    /// id + observing device hostname.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Service, string? Type, string? DeviceId, string? HostFriendlyName, string? HostHostname,
            string? HostIp, string? CaStatus, DateTimeOffset? RootNotAfter,
            long? TotalQueries, double? BlockedPct)> ListServicesAsync(
            this NpgsqlConnection connection,
            string? type,
            string? q,
            string? afterService,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns the identity, CA cert summary, and DNS stats for one service.
    /// Returns no rows if the service id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Service, string? ServiceId, string? Type, string? DeviceId, string?
        CaStatus, string? CaAddress, string? RootSubjectDn, DateTimeOffset? RootNotBefore, DateTimeOffset? RootNotAfter,
        string? RootFingerprint, string? IntSubjectDn, DateTimeOffset? IntNotBefore, DateTimeOffset? IntNotAfter, long?
        TotalQueries, long? TotalBlocked, double? BlockedPct)> GetServiceDetailAsync(
        this NpgsqlConnection connection,
        string service,
        CancellationToken cancellationToken
    );

    /// <summary>CA provisioners for one service.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Provisioner, string? ProvisionerType, string? DefaultDuration)>
        GetServiceProvisionersAsync(
            this NpgsqlConnection connection,
            string service,
            CancellationToken cancellationToken
        );

    /// <summary>DNS zones for one service.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Zone, string? ZoneType)> GetServiceZonesAsync(
        this NpgsqlConnection connection,
        string service,
        CancellationToken cancellationToken
    );

    /// <summary>DHCP scopes for one service.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Scope, bool? Enabled, string? StartAddress, string? EndAddress, string? SubnetMask,
            string?
            Gateway)> GetServiceScopesAsync(
            this NpgsqlConnection connection,
            string service,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// DNS records (A/AAAA/CNAME) for one service, ordered by zone, record, type.
    /// Value is the IP for A/AAAA or the target name for CNAME.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Zone, string Record, string Rtype, string? Value, int? Ttl)> GetServiceRecordsAsync(
            this NpgsqlConnection connection,
            string service,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Logical services hosted on one device (matched by proj_services.device_id),
    /// with CA status/expiry and DNS query stats, ordered by service id.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Service, string? Type, string? CaStatus, DateTimeOffset? RootNotAfter, long?
            TotalQueries, double? BlockedPct)> ListDeviceServicesAsync(
            this NpgsqlConnection connection,
            string deviceId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Resolves a service endpoint IP to the live device hosting it, preferring the device's own
    /// interface IP, then its last-seen IP, then an ARP neighbor sighting. At most one row; a
    /// non-match yields no rows so callers leave <c>proj_services.device_id</c> NULL rather than
    /// guess. Feeds the endpoint-IP → host linkage for remotely-polled services.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ServiceHostDevice> ResolveServiceHostDeviceAsync(
        this NpgsqlConnection connection,
        string ip,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Single-column device-id result for <see cref="ServiceQueries.ResolveServiceHostDeviceAsync" />.
/// A named shape (not a bare <c>string</c>) so the generator's schema validator can bind the
/// <c>device_id</c> column.
/// </summary>
public sealed record ServiceHostDevice(string? DeviceId);