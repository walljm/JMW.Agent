using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class DeviceQueries
{
    // ── Reporting: Devices ──────────────────────────────────────────────────────

    /// <summary>
    /// Lists devices with optional management-status filter, hostname/fingerprint
    /// search, and keyset pagination ordered by (COALESCE(hostname,''), device_id).
    /// Pass null for status/afterHostname/afterDeviceId/search to start unfiltered.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid DeviceId, string? Hostname, string? OsFamily, string? OsDistro, string ManagementStatus,
            DateTimeOffset? LastSeen, string? Vendor)> ListDevicesAsync(
            this NpgsqlConnection connection,
            string? status,
            string? afterHostname,
            string? afterDeviceId,
            string? search,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns the combined identity, OS, vendor, and hardware summary for one device.
    /// Returns no rows if the device id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid DeviceId, string ManagementStatus, string? Hostname, string?
        FriendlyName,
        string? OsFamily,
        string? OsDistro, DateTimeOffset? LastSeen, string?
        Vendor, string? VendorSourceName, string? Kind, string? CpuModel, long? CpuCores, long?
        TotalMemBytes, string?
        SystemVendor, string? SystemModel, string? SystemSerial, string? LastSeenIp)> GetDeviceSummaryAsync(
        this NpgsqlConnection connection,
        Guid deviceId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns the discovery fingerprints for a device, ordered by source then type.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string FpType, string FpValue, string? Source, DateTimeOffset LastSeen)>
        GetDeviceFingerprintsAsync(
            this NpgsqlConnection connection,
            Guid deviceId,
            CancellationToken cancellationToken
        );

    // ── Reporting: Device Detail tabs ───────────────────────────────────────────

    /// <summary>System tab — one row per device, or none if no system facts exist.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? Hostname, string? OsFamily, string? OsDistro, DateTimeOffset
        UpdatedAt)> GetDeviceSystemAsync(
        this NpgsqlConnection connection,
        string device,
        CancellationToken cancellationToken
    );

    /// <summary>Hardware tab — one row per device, or none if no hardware facts exist.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string? CpuModel, string? CpuVendor, long? CpuCores, long? CpuLogicalCores,
        double? CpuMhz, long? TotalMemBytes, string? SystemVendor, string? SystemModel, string? SystemSerial, string?
        BiosVersion, string? Virtualization, DateTimeOffset UpdatedAt)> GetDeviceHardwareAsync(
        this NpgsqlConnection connection,
        string device,
        CancellationToken cancellationToken
    );

    /// <summary>Interfaces tab — network interfaces for a device.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Interface, string? Name, string? MacAddress, string? ObscuredMac, string? Oui, string?
            OuiCountry, string?
            Ipv4, string? Ipv6,
            long? Mtu, bool?
            Up, long? SpeedBps, string?
            Duplex, string? Type, DateTimeOffset UpdatedAt)> GetDeviceInterfacesAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );

    /// <summary>Disks tab — physical disks and SMART health for a device.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Disk, string? Name, string? Model, long? SizeBytes, string? Type, string? SmartHealth,
            double? SmartTempC, DateTimeOffset UpdatedAt)> GetDeviceDisksAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );

    /// <summary>Filesystems tab — mount points with capacity for a device.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Filesystem, string? FsType, long? TotalBytes, long? UsedBytes, long? FreeBytes, double?
            UsedPct, DateTimeOffset UpdatedAt)> GetDeviceFilesystemsAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );

    /// <summary>Containers tab — Docker containers for a device.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string Container, string? Name, string? Image, string? State, string? Health, long?
            RestartCount,
            DateTimeOffset UpdatedAt)> GetDeviceContainersAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );

    /// <summary>Ports tab — listening TCP/UDP endpoints for a device, ordered by port.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string ListeningPort, string? Protocol, string? Address, int? Port, string? ProcessName, long?
            Pid
          , DateTimeOffset UpdatedAt)> GetDevicePortsAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Every current fact known about a device — the "all facts" view. Unions the
    /// device's OWN facts (facts_history keyed by Device) with the SIGHTING facts
    /// observers recorded about it (Discovered[] facts under any observer whose
    /// proj_discovered row resolved to this device's MAC). Latest value per fact id.
    /// </summary>
    // AttributePath/CollectedAt are non-null in every row, but the "identity" UNION
    // branch computes them (a concatenation + a joined column), so the result-set
    // schema reports them nullable; the page model coalesces them back.
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? AttributePath, string? KeyValues, string? Value, string? Origin, string? SourceName,
            DateTimeOffset? CollectedAt)> GetDeviceAllFactsAsync(
            this NpgsqlConnection connection,
            Guid deviceId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// The device's current facts (latest value per fact id) whose latest write came from the
    /// given collector — a failing collector's "blast radius" on this device. See F4.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string AttributePath, string? KeyValues, string? Value, DateTimeOffset
        CollectedAt)> GetDeviceFactsBySourceAsync(
        this NpgsqlConnection connection,
        Guid deviceId,
        string sourceName,
        CancellationToken cancellationToken
    );

    /// <summary>Latest value of every fact keyed to a service — feeds the service-detail fact views.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string AttributePath, string? KeyValues, string? Value)>
        GetServiceAllFactsAsync(
            this NpgsqlConnection connection,
            string service,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Advertised mDNS / Bonjour services for a device (distinct service types across
    /// all observers that saw it), joined via the device's reconstructed MAC.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ServiceResult> GetDeviceAdvertisedServicesAsync(
        this NpgsqlConnection connection,
        Guid deviceId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sightings — how other observers saw this device. Joins proj_discovered rows
    /// (keyed by observer + neighbor IP) to this device via its reconstructed MAC
    /// fingerprint, surfacing the per-observer link telemetry + advertised services.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string ObserverId, string? ObserverHostname, string Ip, string? Sources, string? Oui,
            string? OuiCountry, string? Services)>
        GetDeviceSightingsAsync(
            this NpgsqlConnection connection,
            Guid device,
            CancellationToken cancellationToken
        );

    /// <summary>Components tab — hardware component inventory for a device, ordered by class then slot.</summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string HwComponent, string? Class, string? Slot, string? Description, string? Vendor, string?
            Model, string? Serial, string? Firmware, string? Status, bool? IsFru, DateTimeOffset UpdatedAt)>
        GetDeviceComponentsAsync(
            this NpgsqlConnection connection,
            string device,
            CancellationToken cancellationToken
        );
}