using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class DiscoveryQueries
{
    // ── Discovery Materializer ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all ARP-observed MACs that have no existing 'mac' fingerprint.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredMacResult> GetNewArpMacsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns DHCP lease MACs (service API projection) that have no existing 'mac' fingerprint.
    /// Non-MAC columns are null — returned in the standard 10-column discovered-row shape.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredRow> GetNewDhcpMacsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns local DHCP lease MACs (dnsmasq/ISC/Kea/OpenWrt) that have no existing 'mac' fingerprint.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredRow> GetNewDhcpLocalMacsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns scanner-discovered devices that have a MAC and no existing 'mac' fingerprint.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredRow> GetNewDiscoveredMacsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns scanner-discovered devices without a MAC but with at least one serial/UUID
    /// that does not yet match an existing device fingerprint.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredRow> GetNewDiscoveredSerialsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns all discovered rows carrying an obscured MAC (reconstructed or not),
    /// with the intrinsic attributes the resolved device should inherit. The
    /// materializer reconstructs null-mac rows, then promotes every row.
    /// AgentId (proj_discovered.agent_id — the row's own reporting agent) scopes the
    /// reconstruction's IP→MAC lookup to that agent's own LAN; see
    /// docs/plans/ha-device-enrichment.md §5.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Device, string Ip, string? ObscuredMac, string? Mac, string?
        Hostname, string? Model, string? FriendlyName, string? DeviceType, string? CastId, string? Vendor,
        string? Os, Guid? AgentId)> GetObscuredMacRowsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Promotes a discovered device's vendor/kind onto proj_devices (the unified summary — see
    /// DeviceVendorDerivation for the agent-direct vendor path). COALESCE keeps an existing value
    /// for either column. Returns the device ID.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> UpsertDeviceSummaryAsync(
        this NpgsqlConnection connection,
        string device,
        string? vendor,
        string? kind,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Distinct neighbor-IP count per Cast device id across ALL discovered rows (not
    /// just obscured-MAC ones). A cast id at &gt;1 IP is a stale/roamed advertisement;
    /// counted over the full table because the current device's row may carry the cast
    /// id with no obscured MAC (networkState-only) and would be missed otherwise.
    /// Reads materialization_facts, whose <c>value</c> column is NOT NULL — CastId is
    /// non-nullable here (unlike the old proj_discovered.cast_id-backed query).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string CastId, long? IpCount)> GetCastIdIpCountsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Populates a discovered row's <c>mac</c> with the reconstructed full MAC. This
    /// is the "additional identifying fact" — the normal discovered-MAC path then
    /// materializes/merges the device from it. Returns the observer device id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> SetDiscoveredMacAsync(
        this NpgsqlConnection connection,
        string device,
        string discovered,
        string mac,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns the real full MACs (12 lowercase hex, no separators) the server has
    /// attested for <paramref name="ip" />, scoped to <paramref name="agentId" />'s own
    /// LAN — from that agent's ARP cache, DHCP leases (both service-polled and local), and
    /// previously-discovered non-obscured MACs. Rows with no recorded agent_id (written
    /// before this scoping existed) are treated as unscoped rather than excluded. Pass null
    /// for <paramref name="agentId" /> when the caller's own agent is unknown — this matches
    /// only other unscoped rows, never a row belonging to a known different agent. Used to
    /// reconstruct an obscured Google Wifi MAC by IP join (the caller then corroborates by
    /// OUI), and by the Home Assistant IP-join (see docs/plans/ha-device-enrichment.md §5).
    /// Scoping matters because RFC1918 addresses can be reused across independent LANs this
    /// server ingests from.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DiscoveredMacResult> GetKnownMacsForIpAsync(
        this NpgsqlConnection connection,
        string ip,
        Guid? agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Twin of <see cref="GetKnownMacsForIpAsync" /> that also resolves each candidate's OUI
    /// vendor (see GetKnownMacsWithVendorForIp.sql) — the second corroboration signal for the
    /// Home Assistant IP-join (docs/plans/ha-device-enrichment.md §5), cross-checked against
    /// the HA device's own self-reported manufacturer before a MAC-less device's recovered
    /// identity is accepted.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Mac, string? Vendor)> GetKnownMacsWithVendorForIpAsync(
        this NpgsqlConnection connection,
        string ip,
        Guid? agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns interface rows carrying an obscured MAC and an IPv4 to join on, emitted on
    /// every pass regardless of whether mac_address is already reconstructed. The
    /// materializer reconstructs mac_address on first sight and re-feeds the real MAC into
    /// the AP device resolve each pass so a later-appearing real-MAC observer still merges.
    /// <c>MacAddress</c> is the already-reconstructed real MAC (null until reconstructed).
    /// AgentId (proj_interfaces.agent_id) scopes the reconstruction's IP→MAC lookup to that
    /// agent's own LAN; see docs/plans/ha-device-enrichment.md §5.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Device, string Interface, string? Ip, string? ObscuredMac,
            string? MacAddress, Guid? AgentId)>
        GetInterfaceObscuredMacRowsAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns discovered rows carrying an SSH host-key fingerprint (+ the row's MAC when
    /// present). The materializer resolves each as a <c>FingerprintType.SshHostKey</c>
    /// (unioned with the MAC) so observations of the same host converge onto one device.
    /// Reads materialization_facts, whose <c>value</c> column is NOT NULL — SshHostKey is
    /// non-nullable here (unlike the old proj_discovered.ssh_host_key-backed query).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Mac, string SshHostKey)> GetSshHostKeyRowsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns discovered rows carrying a vendor identity id (Hue bridge id / ONVIF hardware id),
    /// plus the row's MAC when present. The materializer promotes each id to a fingerprint,
    /// unioned with the MAC, so an observer that sees only the id still merges onto the device.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Mac, string? HueBridgeId, string? OnvifHardwareId)>
        GetScannerIdRowsAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Populates an interface row's <c>mac_address</c> with the reconstructed full MAC
    /// (keyed by device + interface). Does not resolve or merge a device — this is the
    /// device's own interface. Returns the device id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> SetInterfaceMacAsync(
        this NpgsqlConnection connection,
        string device,
        string @interface,
        string mac,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upserts hostname, friendly name, last-seen IP, and OS family into proj_systems. COALESCE
    /// ensures a value already set (agent-reported hostname, or a prior promotion/operator
    /// edit of friendly_name) is never overwritten by this path. Returns the device ID.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> UpsertDeviceSystemAsync(
        this NpgsqlConnection connection,
        string device,
        string? hostname,
        string? friendlyName,
        string? lastSeenIp,
        string? osFamily,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upserts vendor, model, and serial into proj_hardware. COALESCE ensures agent-supplied
    /// values are never overwritten. Returns the device ID.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> UpsertDeviceHardwareAsync(
        this NpgsqlConnection connection,
        string device,
        string? vendor,
        string? model,
        string? serial,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Upserts a firmware/software version into proj_hardware. COALESCE ensures a
    /// previously-set value is never overwritten. See
    /// docs/plans/ha-device-enrichment.md §6. Returns the device ID.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<DeviceRefResult> UpsertDeviceFirmwareAsync(
        this NpgsqlConnection connection,
        string device,
        string firmwareVersion,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns whether a projection table exists in the jmwdiscovery schema.
    /// Used to guard materializer queries against databases missing a migration.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<TableExistsResult> ProjectionTableExistsAsync(
        this NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns already-registered devices whose proj_hardware/proj_systems intrinsic fields
    /// (vendor, model, os, hostname, friendly_name) are still null, alongside the best available
    /// proj_discovered value for each — re-evaluated every pass, not just on first mint. See
    /// GetPromotionGapRows.sql.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Device, string? Vendor, string? Model, string? Os, string?
        Hostname, string? FriendlyName)> GetPromotionGapRowsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );
}