using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class ReportingQueries
{
    // ── Reporting: Hosts ────────────────────────────────────────────────────────
    // The Devices list is hand-built in DeviceListApi.QueryAsync because its ORDER BY / keyset
    // cursor are chosen dynamically from a sortable-column allowlist, which a static
    // [DatabaseCommand] cannot express (ORDER BY is not parameterizable).

    // ── Reporting: Open Ports ───────────────────────────────────────────────────

    /// <summary>
    /// Lists listening ports across the fleet, joined to the host's hostname, with optional
    /// port/protocol filters and keyset pagination ordered by (sort key, device, listeningport).
    /// The [SortableBy] declarations produce one schema-validated command-text variant per
    /// column per direction; <c>sort</c>/<c>dir</c> select the variant at runtime (unrecognized
    /// sort falls back to device, the first declared key). SortKey is the SQL-computed sort
    /// expression for the row, used verbatim for the next-page cursor so it can never drift
    /// from the keyset comparison.
    /// </summary>
    [DatabaseCommand]
    [SortableBy("device", "p.device")]
    [SortableBy("port", "lpad(p.port::text, 5, '0')")]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string ListeningPort, string? Protocol, string? Address,
            int? Port, string? ProcessName, long? Pid, string? SortKey, string? FriendlyName)> ListPortsAsync(
            this NpgsqlConnection connection,
            int? port,
            string? proto,
            string? afterSortKey,
            string? afterDevice,
            string? afterListeningPort,
            int limit,
            string? sort,
            string? dir,
            CancellationToken cancellationToken
        );

    // ── Reporting: Containers ───────────────────────────────────────────────────

    /// <summary>
    /// Lists containers across the fleet, joined to the host's hostname, with optional
    /// state/image filters and keyset pagination ordered by (sort key, device, container).
    /// SortKey is the SQL-computed sort expression for the row, used verbatim for the
    /// next-page cursor.
    /// </summary>
    [DatabaseCommand]
    [SortableBy("device", "c.device")]
    [SortableBy("state", "coalesce(c.state, '')")]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string Container, string? Name, string? Image,
            string? State, string? Health, double? CpuPct, long? MemUsageBytes, long? RestartCount,
            string? ComposeProject, string? ComposeService, string? RestartPolicy, string? SortKey,
            string? FriendlyName)> ListContainersAsync(
            this NpgsqlConnection connection,
            string? state,
            string? image,
            string? afterSortKey,
            string? afterDevice,
            string? afterContainer,
            int limit,
            string? sort,
            string? dir,
            CancellationToken cancellationToken
        );

    // ── Reporting: ARP / Network ────────────────────────────────────────────────

    /// <summary>
    /// Lists ARP-observed neighbors across the fleet with MAC→device fingerprint resolution,
    /// with optional IP/MAC search and keyset pagination ordered by (sort key, device, arp).
    /// SortKey is the SQL-computed sort expression for the row, used verbatim for the
    /// next-page cursor.
    /// </summary>
    [DatabaseCommand]
    [SortableBy("ip", "a.arp")]
    [SortableBy("device", "a.device")]
    [SortableBy("mac", "coalesce(a.mac, '')")]
    public static partial
        IAsyncEnumerable<(string Device, string? ObserverHostname, string Ip, string? Mac, string? Iface,
            string? State, Guid? ResolvedDeviceId, string? ResolvedHostname, string? Oui, string? OuiCountry,
            string? SortKey, string? ResolvedFriendlyName)> ListArpAsync(
            this NpgsqlConnection connection,
            string? search,
            string? afterSortKey,
            string? afterDevice,
            string? afterArp,
            int limit,
            string? sort,
            string? dir,
            CancellationToken cancellationToken
        );

    // ── Reporting: Hardware Components ──────────────────────────────────────────

    /// <summary>
    /// Lists hardware inventory components across the fleet (board, CPU, disk, fan, PSU, …),
    /// joined to the host's hostname, with optional search/class filters and keyset pagination
    /// ordered by (sort key, device, hwcomponent). The hostname sort key lives on proj_devices
    /// (the resolved identity column, context-derivations.md §3.3) so it can drive the plan
    /// through proj_devices_hostname_sort_idx. SortKey is the SQL-computed sort expression for
    /// the row, used verbatim for the next-page cursor.
    /// </summary>
    [DatabaseCommand]
    [SortableBy("hostname", "coalesce(pdv.hostname, '')")]
    [SortableBy("class", "coalesce(c.class, '')")]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string Hwcomponent, string? Class, string? Slot,
            string? Description, string? Vendor, string? Model, string? Serial, string? Firmware, string? Status,
            bool? IsFru, string? SortKey, string? FriendlyName)> ListComponentsAsync(
            this NpgsqlConnection connection,
            string? search,
            string? cls,
            string? afterSortKey,
            string? afterDevice,
            string? afterComponent,
            int limit,
            string? sort,
            string? dir,
            CancellationToken cancellationToken
        );

    // ── Reporting: Subnets ──────────────────────────────────────────────────────

    /// <summary>
    /// Lists agent-observed interfaces that carry an IPv4 CIDR, or a bare IPv4 plus a separately
    /// captured prefix length (device, interface name, hostname), the query-time source for the
    /// "I" (interface) subnet signal on the Subnets page. Ipv4PrefixLength is populated only by
    /// collectors (Google Wifi/OnHub) that must emit a bare Ipv4 — SubnetsApi synthesizes
    /// "{Ipv4}/{Ipv4PrefixLength}" for those rows before parsing.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string? Name, string? Ipv4, int? Ipv4PrefixLength)>
        ListSubnetInterfacesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Distinct IPv4 addresses across every projection that carries one (interfaces, ARP, DHCP
    /// leases, discovered devices) — the candidate pool for per-subnet host counts on the
    /// Subnets page. Grouping into subnets happens in <c>SubnetsApi</c>, not here.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<SubnetHostIp> ListSubnetHostIpsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves a single IP (typically a subnet gateway) to its MAC and, if fingerprinted, its
    /// known device. At most one row.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Mac, Guid? ResolvedDeviceId, string? ResolvedHostname, string? Oui)>
        ResolveIpDeviceAsync(
            this NpgsqlConnection connection,
            string ip,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Connected/static IPv4 routes (excludes the default route) — fills subnet-membership gaps
    /// that single-valued <c>proj_interfaces.ipv4</c> can miss. The "R" subnet signal. The
    /// interface name (<c>iface</c>) lets <c>SubnetsApi</c> recognize a host-local Docker bridge
    /// (docker0/br-&lt;hex&gt;) from the route table alone — no Docker API access required.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Device, string Destination, string? Iface)> ListSubnetRoutesAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Each device's IPv4 default-route gateway — fills a subnet's gateway when no DHCP scope
    /// provides one, and feeds the L3 graph's "Internet" edge detection (Iface labels that edge).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string? Gateway, string? Iface)>
        ListDefaultRoutesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Docker network subnets per host (device, subnet CIDR, network name, driver, scope). Lets
    /// <c>SubnetsApi</c> classify a subnet as host-local NAT (<c>driver='bridge'</c>) vs. routable
    /// (macvlan/ipvlan/overlay) and key host-local CIDRs per-host — see docs/plans/l3-topology.md
    /// Track 1. Cidr is the row's key dimension and is never null.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Device, string Cidr, string? Name, string? Driver, string? Scope)>
        ListDockerNetworksAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    // ── Reporting: L2 Topology ───────────────────────────────────────────────────

    /// <summary>
    /// Device[].Neighbor[] facts (LLDP-derived L2 adjacency) across every device, pivoted into
    /// one row per (device, neighbor) — the edge source for the L2 topology graph.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Device, string? NeighborKey, string? Hostname, string? LocalPort,
        string? RemoteChassisId, string? RemotePortId, string? RemoteSysName, string? RemoteMac, string? RemoteIp,
        string? Protocol)> ListNeighborFactsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Clients relayed through a Google Wifi/OnHub mesh point (real BSSID) — the L2 topology
    /// graph's source for grouping clients under a synthetic mesh-point node when the mesh
    /// point itself isn't independently known as a device.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Mac, string? ObscuredMac, string? Hostname, string? MeshApBssid)>
        ListMeshRelayedClientsAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Resolves a MAC address directly to its known device — used when an LLDP neighbor's
    /// remote identity is only a chassis MAC (no management IP advertised). At most one row.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid ResolvedDeviceId, string? ResolvedHostname)> ResolveMacDeviceAsync(
        this NpgsqlConnection connection,
        string mac,
        CancellationToken cancellationToken
    );

    // ── Reporting: Change Feed ──────────────────────────────────────────────────

    /// <summary>
    /// Lists recent fact changes from facts_history, newest first, with the
    /// device's hostname resolved from key_values->>'Device'. Supports a
    /// since-timestamp lower bound, a device filter, and keyset pagination
    /// ordered by (collected_at DESC, id ASC).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Id, string AttributePath, string? KeyValues, short Kind, string? ValueStr, long?
            ValueLong,
            double? ValueDouble, DateTimeOffset CollectedAt, string? Hostname, string? FriendlyName)> ListChangesAsync(
            this NpgsqlConnection connection,
            DateTimeOffset? since,
            string? deviceId,
            DateTimeOffset? afterCollectedAt,
            string? afterId,
            int limit,
            CancellationToken cancellationToken
        );

    // ── Reporting: Storage Health ───────────────────────────────────────────────

    /// <summary>
    /// Lists physical disks across the fleet with SMART health, joined to the
    /// host's hostname. Supports device/hostname/model/type search and keyset
    /// pagination ordered by (device, disk).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Device, string? Hostname, string Disk, string? Name, string? Model,
        string? Type, string? SmartHealth, double? SmartTempC, double? SmartWearPct, long? SmartPowerOnHours, long?
        SizeBytes, string? FriendlyName)> ListStorageDisksAsync(
        this NpgsqlConnection connection,
        string? search,
        string? afterDevice,
        string? afterDisk,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists filesystems across the fleet with capacity/usage, joined to the
    /// host's hostname. Supports device/hostname/mount/type search and keyset
    /// pagination ordered by (device, filesystem).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Device, string? Hostname, string Filesystem, string? FsType, long? TotalBytes, long?
            UsedBytes, long? FreeBytes, double? UsedPct, string? FriendlyName)> ListStorageFilesystemsAsync(
            this NpgsqlConnection connection,
            string? search,
            string? afterDevice,
            string? afterFilesystem,
            int limit,
            CancellationToken cancellationToken
        );

    // ── Reporting: DHCP Local Leases (Terrain) ───────────────────────────────────

    /// <summary>
    /// Lists DHCP leases read from local lease files across the fleet (dnsmasq,
    /// isc-dhcpd, kea, openwrt). Supports IP/MAC/hostname search and keyset
    /// pagination ordered by (device, lease).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Device, string? ObserverHostname, string Mac, string? Oui, string? OuiCountry, string?
            Ip, string? ClientHostname,
            string? ExpiresAt, string? Source)> ListDhcpLeasesAsync(
            this NpgsqlConnection connection,
            string? search,
            string? afterDevice,
            string? afterLease,
            int limit,
            CancellationToken cancellationToken
        );
    // ── Reporting: DNS Records (Terrain) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists DNS resource records (A/AAAA/CNAME) across all services. Value is the
    /// IP for A/AAAA or the target name for CNAME. Supports record/value/zone search
    /// and keyset pagination ordered by (service, zone, record, rtype).
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Service, string Zone, string Record, string Rtype, string? Value, int? Ttl)>
        ListDnsRecordsAsync(
            this NpgsqlConnection connection,
            string? search,
            string? afterService,
            string? afterZone,
            string? afterRecord,
            string? afterRtype,
            int limit,
            CancellationToken cancellationToken
        );

    // ── Reporting: Terrain summary cards ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns aggregate counts for the terrain page summary strip: DNS server
    /// count + query totals, active DHCP scope count, local lease count, distinct
    /// known-CA count and how many of those expire within 30 days.
    /// Always returns exactly one row.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? DnsServerCount, long? TotalQueries, long? TotalBlocked, long? ActiveScopeCount,
            long? LocalLeaseCount, long? CaCount, long? CaExpiringCount)> GetTerrainSummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Per-service DNS query statistics for the DNS tab summary.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Service, long? TotalQueries, long? TotalBlocked, double? BlockedPct,
            DateTimeOffset UpdatedAt)> ListTerrainDnsServicesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>All DHCP scopes across all services for the DHCP tab summary.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Service, string Scope, bool? Enabled, string? StartAddress, string? EndAddress,
            string? SubnetMask, string? Gateway)> ListTerrainDhcpScopesAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Every distinct CA known in the fleet, unified across operated CA services, CA certs
    /// trusted on some host, and issuers only ever observed signing another cert. See
    /// ListTerrainCaInventory.sql for the derivation. Optional search over subject DN,
    /// fingerprint, or the operating service id.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Kind, string? Subtype, string? SubjectDn, string? Fingerprint,
            DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, string? ServiceRef, long? SeenOnCount)>
        ListTerrainCaInventoryAsync(
            this NpgsqlConnection connection,
            string? search,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// The Certificate Authority services operating in the network — one row per CA service
    /// (proj_service_ca), resolved to its host device. See ListCaServices.sql. Narrower than
    /// <see cref="ListTerrainCaInventoryAsync" /> (which lists every CA certificate anywhere):
    /// this is the actual CAs we run/observe as services. Optional search over subject DN,
    /// status, or host name.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string ServiceRef, string? HostName, string? Status, string? Address,
            string? RootSubjectDn, DateTimeOffset? RootNotBefore, DateTimeOffset? RootNotAfter,
            string? RootFingerprint, string? IntSubjectDn, DateTimeOffset? IntNotBefore,
            DateTimeOffset? IntNotAfter, long? ProvisionerCount)>
        ListCaServicesAsync(
            this NpgsqlConnection connection,
            string? search,
            CancellationToken cancellationToken
        );

    // ── Reporting: Dashboard ────────────────────────────────────────────────────

    /// <summary>
    /// Returns fleet summary counts for the dashboard in a single round-trip.
    /// Counts are nullable because COUNT(*) is reported as nullable in SchemaOnly mode.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(long? TotalDevices, long? ManagedDevices, long? DiscoveredDevices, long? TotalAgents, long?
            ApprovedAgents, long? PendingAgents)> GetDashboardSummaryAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );
}

/// <summary>
/// Single-column result shape for <see cref="ReportingQueries.ListSubnetHostIpsAsync" />. A bare
/// scalar <c>IAsyncEnumerable&lt;string&gt;</c> return doesn't carry a column name for the
/// generator's schema validator to check against — this named-constructor shape does.
/// </summary>
public sealed record SubnetHostIp(string? Ip);