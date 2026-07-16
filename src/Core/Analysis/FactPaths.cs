namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Canonical attribute_path constants for all facts in the JMW Agent system.
/// Empty brackets [] mark list positions — the key is provided at collection time.
/// Path conventions:
/// - All speeds are in BPS (collector converts LinkSpeedMbps × 1_000_000)
/// - All bytes are raw uint64 values
/// - All percentages are float64 (0–100)
/// - Paths mirror the Go struct hierarchy: Device[].OS.*, Device[].Hardware.*, etc.
/// - Derived facts share the same namespace as observed facts — a fact may be
/// observed on one device and derived on another (no "Derived." prefix)
/// </summary>
public static class FactPaths
{
    // ── Device identity (from Sighting) ───────────────────────────────────────
    public const string DeviceVendor = "Device[].Vendor";
    public const string DeviceKind = "Device[].Kind";

    // ── Operator-authored data (FactSource.ManualEntry only — no collector writes here) ──
    // The list key is a user-chosen slug (a custom_field_definitions row), not a collector
    // identifier. See docs/plans/user-provided.md and FactViewLibrary's "Custom Fields" view.
    public const string CustomFieldValue = "Device[].Custom[].Value";

    // ── System / real-time metrics (MetricSnapshot) ───────────────────────────
    public const string SystemCpuPercent = "Device[].System.CpuPercent";
    public const string SystemMemUsedBytes = "Device[].System.MemUsedBytes";
    public const string SystemMemTotalBytes = "Device[].System.MemTotalBytes";
    public const string SystemLoad1 = "Device[].System.Load1";
    public const string SystemLoad5 = "Device[].System.Load5";
    public const string SystemLoad15 = "Device[].System.Load15";
    public const string SystemUptimeSeconds = "Device[].System.UptimeSeconds";

    // ── OS info (Inventory.OS) ────────────────────────────────────────────────
    public const string SystemHostname = "Device[].OS.Hostname";
    public const string SystemOsFamily = "Device[].OS.Family";
    public const string SystemOsDistro = "Device[].OS.Distro";
    public const string SystemOsVersion = "Device[].OS.Version";
    public const string SystemOsBuild = "Device[].OS.Build";
    public const string SystemKernel = "Device[].OS.Kernel";
    public const string SystemKernelArch = "Device[].OS.KernelArch";
    public const string SystemTimezone = "Device[].OS.Timezone";
    public const string SystemBootTime = "Device[].OS.BootTime";
    public const string SystemInstallDate = "Device[].OS.InstallDate";

    // ── Hardware (Inventory.Hardware) ─────────────────────────────────────────
    public const string HwCpuModel = "Device[].Hardware.CpuModel";
    public const string HwCpuVendor = "Device[].Hardware.CpuVendor";
    public const string HwCpuCores = "Device[].Hardware.CpuCores";
    public const string HwCpuLogicalCores = "Device[].Hardware.CpuLogicalCores";
    public const string HwCpuMhz = "Device[].Hardware.CpuMhz";
    public const string HwTotalMemBytes = "Device[].Hardware.TotalMemBytes";
    public const string HwBoardVendor = "Device[].Hardware.BoardVendor";
    public const string HwBoardModel = "Device[].Hardware.BoardModel";
    public const string HwSystemVendor = "Device[].Hardware.SystemVendor";
    public const string HwSystemModel = "Device[].Hardware.SystemModel";
    public const string HwSystemSerial = "Device[].Hardware.SystemSerial";
    public const string HwBiosVersion = "Device[].Hardware.BiosVersion";
    public const string HwBiosDate = "Device[].Hardware.BiosDate";
    public const string HwVirtualization = "Device[].Hardware.Virtualization";
    public const string HwOSArch = "Device[].Hardware.OSArch";

    public const string HwBiosVendor = "Device[].Hardware.BiosVendor";

    // Chassis (SMBIOS type 3) — Linux dmidecode -q "Chassis Information". No projection
    // column (peer to BoardVendor/BiosVendor); surfaced via the "Hardware Details" fact view.
    public const string HwChassisVendor = "Device[].Hardware.ChassisVendor";
    public const string HwChassisType = "Device[].Hardware.ChassisType";
    public const string HwChassisSerial = "Device[].Hardware.ChassisSerial";
    public const string HwChassisAssetTag = "Device[].Hardware.ChassisAssetTag";

    // Key = thermal zone type (e.g. "x86_pkg_temp") or chip/label (e.g. "coretemp/Core 0")
    public const string HwTemperatureCelsius = "Device[].Hardware.Temperature[].Celsius";

    // ── Interface — static config (Inventory.Network.Interfaces[]) ───────────
    // Key = MAC address (normalized: 12 lowercase hex chars, no separators)
    public const string InterfaceName = "Device[].Interface[].Name";
    public const string InterfaceMAC = "Device[].Interface[].MAC";
    public const string InterfacePermMAC = "Device[].Interface[].PermMAC";

    // Obscured interface MAC (Google Wifi masks the device bytes with '*'). Kept as a
    // raw fact — never a fingerprint; the DiscoveryMaterializer reconstructs the real
    // MAC into InterfaceMAC by IP + OUI, mirroring the Discovered[].ObscuredMAC path.
    public const string InterfaceObscuredMAC = "Device[].Interface[].ObscuredMAC";
    public const string InterfaceMTU = "Device[].Interface[].MTU";
    public const string InterfaceUp = "Device[].Interface[].Up";
    public const string InterfaceLoopback = "Device[].Interface[].Loopback";
    public const string InterfaceSpeedBps = "Device[].Interface[].SpeedBps"; // collector converts Mbps × 1_000_000
    public const string InterfaceDuplex = "Device[].Interface[].Duplex";
    public const string InterfaceType = "Device[].Interface[].Type";

    // ── Interface — real-time counters (MetricSnapshot.Interfaces[]) ─────────
    // Key = MAC address (same as static config; merged by collector)
    public const string InterfaceRxBytes = "Device[].Interface[].RxBytes";
    public const string InterfaceTxBytes = "Device[].Interface[].TxBytes";
    public const string InterfaceRxPackets = "Device[].Interface[].RxPackets";
    public const string InterfaceTxPackets = "Device[].Interface[].TxPackets";

    // ── Disk (Inventory.Disks[]) ──────────────────────────────────────────────
    // Key = serial number; fall back to disk name if no serial
    public const string DiskName = "Device[].Disk[].Name";
    public const string DiskModel = "Device[].Disk[].Model";
    public const string DiskSizeBytes = "Device[].Disk[].SizeBytes";
    public const string DiskType = "Device[].Disk[].Type";
    public const string DiskSerial = "Device[].Disk[].Serial";
    public const string DiskRemovable = "Device[].Disk[].Removable";

    public const string DiskSmartOverallHealth = "Device[].Disk[].Smart.OverallHealth";
    public const string DiskSmartTempC = "Device[].Disk[].Smart.TempC";
    public const string DiskSmartPowerOnHours = "Device[].Disk[].Smart.PowerOnHours";
    public const string DiskSmartPowerCycles = "Device[].Disk[].Smart.PowerCycles";
    public const string DiskSmartReallocSectors = "Device[].Disk[].Smart.ReallocatedSectors";
    public const string DiskSmartUncorrErrors = "Device[].Disk[].Smart.UncorrectableErrors";
    public const string DiskSmartWearPercent = "Device[].Disk[].Smart.WearPercent"; // SSD/NVMe wear (0=new, 100=spent)
    public const string DiskSmartPendingSectors = "Device[].Disk[].Smart.PendingSectors";

    public const string
        DiskSmartCrcErrors = "Device[].Disk[].Smart.CrcErrors"; // UDMA_CRC_Error_Count (id 199) — cable/link

    public const string
        DiskSmartPercentageUsed = "Device[].Disk[].Smart.PercentageUsed"; // NVMe endurance (0=new, 100=spent)

    public const string
        DiskSmartAvailableSparePct = "Device[].Disk[].Smart.AvailableSparePct"; // NVMe spare blocks remaining

    public const string DiskSmartDataReadGB = "Device[].Disk[].Smart.DataUnitsReadGB";
    public const string DiskSmartDataWrittenGB = "Device[].Disk[].Smart.DataUnitsWrittenGB";

    // ── Network-level DNS config (Inventory.Network) ─────────────────────────
    // Key = 0-based index (string). Emitted by NetworkCollector for global DNS settings.
    public const string NetworkDnsServer = "Device[].Network.DNS[]";

    public const string NetworkDnsSearch = "Device[].Network.DNSSearch[]";

    // Per-interface DNS server (key = interface name, value = DNS IP)
    public const string InterfaceDns = "Device[].Interface[].DNS";
    public const string InterfaceGateway = "Device[].Interface[].Gateway"; // T2-8
    public const string InterfaceDhcpServer = "Device[].Interface[].DhcpServer"; // T2-8 (Windows)

    // WAN provisioning mode (key = interface name). Google Wifi/OnHub: raw device token
    // ("DHCP" | "STATIC" | "PPPOE") from the diagnostic report's wan_configuration.
    public const string InterfaceConnectionType = "Device[].Interface[].ConnectionType";

    // Raw ISP classification token from the router (e.g. "ISP_NONE"). Google Wifi/OnHub only.
    public const string InterfaceIspType = "Device[].Interface[].IspType";

    // ── WAN speed test (Google Wifi/OnHub's own periodic on-device measurement) ──
    public const string NetworkWanSpeedTestAt = "Device[].Network.WanSpeedTest.At";
    public const string NetworkWanSpeedTestDownloadBps = "Device[].Network.WanSpeedTest.DownloadBytesPerSec";
    public const string NetworkWanSpeedTestUploadBps = "Device[].Network.WanSpeedTest.UploadBytesPerSec";
    public const string NetworkWanSpeedTestTotalDownloadedBytes = "Device[].Network.WanSpeedTest.TotalDownloadedBytes";
    public const string NetworkWanSpeedTestTotalUploadedBytes = "Device[].Network.WanSpeedTest.TotalUploadedBytes";

    // ── Filesystem (Inventory.Filesystems[]) ─────────────────────────────────
    // Key = mountpoint path
    public const string FsType = "Device[].Filesystem[].FsType";
    public const string FsTotalBytes = "Device[].Filesystem[].TotalBytes";
    public const string FsUsedBytes = "Device[].Filesystem[].UsedBytes";
    public const string FsFreeBytes = "Device[].Filesystem[].FreeBytes";

    // ── Docker engine (Inventory.Docker) ──────────────────────────────────────
    public const string DockerVersion = "Device[].Docker.Version";
    public const string DockerApiVersion = "Device[].Docker.ApiVersion";
    public const string DockerStorageDriver = "Device[].Docker.StorageDriver";
    public const string DockerContainersRunning = "Device[].Docker.ContainersRunning";
    public const string DockerContainersPaused = "Device[].Docker.ContainersPaused";
    public const string DockerContainersStopped = "Device[].Docker.ContainersStopped";
    public const string DockerImages = "Device[].Docker.Images";
    public const string DockerOS = "Device[].Docker.OS";
    public const string DockerKernel = "Device[].Docker.Kernel";
    public const string DockerMemBytes = "Device[].Docker.MemBytes";
    public const string DockerCpuCount = "Device[].Docker.CPUCount";

    // ── Container (Inventory.Docker.Containers[]) ─────────────────────────────
    // Key = short container ID (first 12 chars of the 64-char full ID)
    public const string ContainerName = "Device[].Container[].Name";
    public const string ContainerImage = "Device[].Container[].Image";
    public const string ContainerState = "Device[].Container[].State";
    public const string ContainerHealth = "Device[].Container[].Health";
    public const string ContainerCpuPercent = "Device[].Container[].CpuPercent";
    public const string ContainerMemUsageBytes = "Device[].Container[].MemUsageBytes";
    public const string ContainerRestartCount = "Device[].Container[].RestartCount";
    public const string ContainerComposeProject = "Device[].Container[].ComposeProject";
    public const string ContainerComposeService = "Device[].Container[].ComposeService";
    public const string ContainerRestartPolicy = "Device[].Container[].RestartPolicy";
    public const string ContainerStatus = "Device[].Container[].Status"; // detailed status string (e.g. "Up 3 hours")

    public const string ContainerCreated = "Device[].Container[].Created"; // Unix timestamp

    // T2-5 cheap already-paid round-trips (present in /containers/json, previously dropped).
    public const string ContainerPorts = "Device[].Container[].Ports"; // "0.0.0.0:8080->80/tcp, ..."
    public const string ContainerMounts = "Device[].Container[].Mounts"; // "src:dst, ..."
    public const string ContainerLabels = "Device[].Container[].Labels"; // "k=v, ..."

    // ── Security (Inventory.Security) ─────────────────────────────────────────
    public const string SecurityFirewallEnabled = "Device[].Security.FirewallEnabled";
    public const string SecurityFirewallProvider = "Device[].Security.FirewallProvider";

    public const string
        SecurityFirewallProfile = "Device[].Security.FirewallProfile[]"; // Windows per-profile state (on/off)

    public const string SecurityAvName = "Device[].Security.AvName"; // primary AV product
    public const string SecurityAvEnabled = "Device[].Security.AvEnabled";
    public const string SecurityAvUpToDate = "Device[].Security.AvUpToDate";
    public const string SecuritySecureBoot = "Device[].Security.SecureBoot";
    public const string SecurityTpmPresent = "Device[].Security.TpmPresent";
    public const string SecurityTpmVersion = "Device[].Security.TpmVersion";
    public const string SecuritySeLinuxMode = "Device[].Security.SeLinuxMode";
    public const string SecurityAppArmor = "Device[].Security.AppArmor";

    public const string SecuritySip = "Device[].Security.SIP"; // macOS System Integrity Protection

    // Encrypted volumes — key = device path (Linux/Windows) or "/" (macOS FileVault)
    public const string SecurityEncryptedVolumeDevice = "Device[].Security.EncryptedVolume[].Device";
    public const string SecurityEncryptedVolumeMountpoint = "Device[].Security.EncryptedVolume[].Mountpoint";
    public const string SecurityEncryptedVolumeType = "Device[].Security.EncryptedVolume[].Type";

    public const string SecurityEncryptedVolumeStatus = "Device[].Security.EncryptedVolume[].Status";

    // Windows Defender
    public const string SecurityDefenderEnabled = "Device[].Security.Defender.Enabled";
    public const string SecurityDefenderRealtimeProtected = "Device[].Security.Defender.RealtimeProtected";
    public const string SecurityDefenderSignatureAgeDays = "Device[].Security.Defender.SignatureAgeDays";
    public const string SecurityDefenderSignatureVersion = "Device[].Security.Defender.SignatureVersion";
    public const string SecurityGatekeeper = "Device[].Security.Gatekeeper"; // macOS Gatekeeper state

    // ── Battery (Inventory.Chassis.Battery) ───────────────────────────────────
    public const string BatteryDesignCapWh = "Device[].Battery.DesignCapacityWh";
    public const string BatteryCurrentCapWh = "Device[].Battery.CurrentCapacityWh";
    public const string BatteryCycleCount = "Device[].Battery.CycleCount";
    public const string BatteryState = "Device[].Battery.State";
    public const string BatteryChargePercent = "Device[].Battery.ChargePercent";

    // ── Updates (Inventory.Updates) ───────────────────────────────────────────
    public const string UpdateManager = "Device[].Updates.Manager";
    public const string UpdatePendingCount = "Device[].Updates.Pending";
    public const string UpdateSecurityCount = "Device[].Updates.Security";

    public const string UpdateRebootRequired = "Device[].Updates.RebootRequired";

    // Pending package list — key = package name. Capped at 50 per collector.
    public const string UpdatePendingName = "Device[].Updates.Pending[].Name";
    public const string UpdatePendingNewVersion = "Device[].Updates.Pending[].NewVersion";
    public const string UpdatePendingSource = "Device[].Updates.Pending[].Source";
    public const string UpdatePendingSecurity = "Device[].Updates.Pending[].Security";

    // ── Routes ───────────────────────────────────────────────────────────────
    // Key dimension: destination CIDR (e.g. "0.0.0.0/0", "192.168.1.0/24")
    public const string RouteGateway = "Device[].Route[].Gateway";
    public const string RouteInterface = "Device[].Route[].Interface";
    public const string RouteMetric = "Device[].Route[].Metric";
    public const string RouteProto = "Device[].Route[].Proto"; // T2-8 — how the route was learned (dhcp/kernel/static)
    public const string RouteSource = "Device[].Route[].Source"; // T2-8 — preferred source IP
    public const string RouteScope = "Device[].Route[].Scope"; // T2-8 — link/host/global
    public const string RouteFamily = "Device[].Route[].Family";

    // ── ARP / neighbor cache ──────────────────────────────────────────────────
    // Key dimension: IP address
    public const string ArpMac = "Device[].ARP[].MAC";
    public const string ArpInterface = "Device[].ARP[].Interface";
    public const string ArpState = "Device[].ARP[].State";

    // ── L2 neighbor adjacency (LLDP; from SnmpCollector) ─────────────────────
    // A port adjacency is a property of the link, not either device alone — same
    // principle as Discovered[].Link.* below. Key dimension: "{localPortNum}-{neighborKey}"
    // (neighborKey = remote IP, MAC, or chassis ID — whichever is available), stable
    // across polls (does not include LLDP's own timeMark).
    public const string NeighborLocalPort = "Device[].Neighbor[].LocalPort";
    public const string NeighborRemoteChassisId = "Device[].Neighbor[].RemoteChassisId";
    public const string NeighborRemotePortId = "Device[].Neighbor[].RemotePortId";
    public const string NeighborRemoteSysName = "Device[].Neighbor[].RemoteSysName";
    public const string NeighborRemoteMac = "Device[].Neighbor[].RemoteMac";
    public const string NeighborRemoteIp = "Device[].Neighbor[].RemoteIp";
    public const string NeighborProtocol = "Device[].Neighbor[].Protocol"; // "lldp" today; future-proofed for cdp

    // ── Device certificates ───────────────────────────────────────────────────
    // Key dimension: SHA-256 fingerprint (lowercase hex, no colons)
    public const string CertSubjectDn = "Device[].Cert[].SubjectDn";
    public const string CertIssuerDn = "Device[].Cert[].IssuerDn";
    public const string CertNotBefore = "Device[].Cert[].NotBefore";
    public const string CertNotAfter = "Device[].Cert[].NotAfter";
    public const string CertPath = "Device[].Cert[].Path";
    public const string CertIsCA = "Device[].Cert[].IsCA";

    public const string CertSANs = "Device[].Cert[].SANs";

    // T2-7 certificate detail.
    public const string CertSerial = "Device[].Cert[].Serial";
    public const string CertSigAlgo = "Device[].Cert[].SigAlgo";
    public const string CertKeySize = "Device[].Cert[].KeySize";
    public const string CertKeyUsage = "Device[].Cert[].KeyUsage";
    public const string CertEku = "Device[].Cert[].Eku";

    // ── CA trust (configured on this device) ──────────────────────────────────
    // Key dimension: root CA fingerprint
    public const string TrustedCaCaUrl = "Device[].TrustedCA[].CaUrl";
    public const string TrustedCaRootPath = "Device[].TrustedCA[].RootPath";

    // ── Hardware component inventory ─────────────────────────────────────────
    // Key dimension: stable component identifier.
    //   Local agents:    slot path ("DIMM_A1"), PCI bus address ("0000:00:1f.2"),
    //                    IPMI sensor name ("Fan1"), dmidecode handle ("0x0038")
    //   Network devices: SNMP entPhysicalIndex, CLI slot path ("chassis/2/module/1")
    //
    // class values: cpu, memory, storage, fan, psu, transceiver, nic, module,
    //               chassis, sensor, port, other
    // status values: ok, failed, absent, unknown
    //
    // details: JSON string of type-specific attributes not captured by the
    //          universal columns. Examples:
    //   memory:      {"size_bytes":17179869184,"speed_mhz":3200,"memory_type":"DDR4"}
    //   fan:         {"rpm":2400,"rpm_low_threshold":800}
    //   psu:         {"capacity_watts":750,"input_voltage":120.0}
    //   transceiver: {"wavelength_nm":1310,"tx_power_dbm":-2.5,"rx_power_dbm":-3.1}
    //   module:      {"port_count":48}
    public const string HwComponentClass = "Device[].HwComponent[].Class";
    public const string HwComponentSlot = "Device[].HwComponent[].Slot";
    public const string HwComponentDescription = "Device[].HwComponent[].Description";
    public const string HwComponentVendor = "Device[].HwComponent[].Vendor";
    public const string HwComponentModel = "Device[].HwComponent[].Model";
    public const string HwComponentSerial = "Device[].HwComponent[].Serial";
    public const string HwComponentFirmware = "Device[].HwComponent[].Firmware";
    public const string HwComponentStatus = "Device[].HwComponent[].Status";
    public const string HwComponentIsFru = "Device[].HwComponent[].IsFru";
    public const string HwComponentDetails = "Device[].HwComponent[].Details";

    // ── Processes (top 25 by CPU time) ────────────────────────────────────────
    // Key dimension: PID (as string)
    public const string ProcessName = "Device[].Process[].Name";
    public const string ProcessCpuTimeSecs = "Device[].Process[].CpuTimeSecs";
    public const string ProcessMemBytes = "Device[].Process[].MemBytes";

    // ── Listening ports ───────────────────────────────────────────────────────
    // Key dimension: "proto:addr:port" e.g. "tcp:0.0.0.0:22", "udp6:::53"
    public const string PortProtocol = "Device[].ListeningPort[].Protocol";
    public const string PortAddress = "Device[].ListeningPort[].Address";
    public const string PortNumber = "Device[].ListeningPort[].Port";
    public const string PortProcessName = "Device[].ListeningPort[].ProcessName";
    public const string PortPid = "Device[].ListeningPort[].PID";

    // ── System services (failed / degraded only) ──────────────────────────────
    // Key dimension: service/unit name (e.g. "nginx.service", "sshd")
    public const string ServiceName = "Device[].Service[].Name";
    public const string ServiceActiveState = "Device[].Service[].ActiveState";
    public const string ServiceSubState = "Device[].Service[].SubState";
    public const string ServiceExitCode = "Device[].Service[].ExitCode";
    public const string ServiceDisplayName = "Device[].Service[].DisplayName";

    // ── Local user accounts ───────────────────────────────────────────────────
    // Key dimension: username. UID is a string to accommodate Windows SIDs.
    public const string LocalUserUsername = "Device[].LocalUser[].Username";
    public const string LocalUserUid = "Device[].LocalUser[].UID";
    public const string LocalUserGid = "Device[].LocalUser[].GID";
    public const string LocalUserHome = "Device[].LocalUser[].Home";
    public const string LocalUserShell = "Device[].LocalUser[].Shell";
    public const string LocalUserIsAdmin = "Device[].LocalUser[].IsAdmin";
    public const string LocalUserDisabled = "Device[].LocalUser[].Disabled";

    // ── Active login sessions ─────────────────────────────────────────────────
    // Key dimension: "user@tty" e.g. "jason@pts/0"
    public const string SessionUser = "Device[].Session[].User";
    public const string SessionTty = "Device[].Session[].TTY";
    public const string SessionLoginAt = "Device[].Session[].LoginAt";
    public const string SessionHost = "Device[].Session[].Host";

    // ── Interface extras ──────────────────────────────────────────────────────
    // IPv4/IPv6 with prefix length (e.g. "192.168.1.5/24")
    public const string InterfaceIPv4 = "Device[].Interface[].IPv4";

    public const string InterfaceIPv6 = "Device[].Interface[].IPv6";

    // CIDR prefix length for collectors (e.g. OnHub) that must emit a bare IP for
    // InterfaceIPv4/IPv6 — that bare-IP meaning is an exact-match join key elsewhere
    // (DiscoveryMaterializer's MAC reconstruction) — but still have the prefix length
    // available from the source data. proj_interfaces falls back to synthesizing
    // "{ipv4}/{prefixLen}" from these when ipv4/ipv6 itself carries no "/".
    public const string InterfaceIPv4PrefixLength = "Device[].Interface[].IPv4PrefixLength";

    public const string InterfaceIPv6PrefixLength = "Device[].Interface[].IPv6PrefixLength";

    // Operator-assigned description (ifAlias via SNMP ifXTable, or equivalent)
    public const string InterfaceAlias = "Device[].Interface[].Alias";

    // SNMP interface admin/oper state (from SnmpDeviceCollector)
    public const string InterfaceAdminStatus = "Device[].Interface[].AdminStatus"; // "up" | "down" | "testing"
    public const string InterfaceOperStatus = "Device[].Interface[].OperStatus"; // "up" | "down" | "dormant" | ...

    // ── L2: bridge/VLAN/STP membership (docs/plans/d3-l2-l3.md) ──────────────
    // Populated by any of OnHub (brctl/swconfig parsing), SNMP (Q-BRIDGE-MIB/BRIDGE-MIB),
    // or Linux/SSH (ip -d link show / bridge vlan show) — same paths regardless of source
    // so the reporting layer stays source-agnostic.
    public const string InterfaceVlanId = "Device[].Interface[].VlanId"; // access/native VLAN or PVID
    public const string InterfaceTaggedVlans = "Device[].Interface[].TaggedVlans"; // trunk port VLAN membership, comma-joined
    public const string InterfaceBridgeMaster = "Device[].Interface[].BridgeMaster"; // bridge this port belongs to, if any
    public const string InterfaceStpState = "Device[].Interface[].StpState"; // forwarding | blocking | disabled | ...
    public const string InterfaceStpRole = "Device[].Interface[].StpRole"; // root | designated | alternate | backup | disabled
    public const string InterfaceStpCost = "Device[].Interface[].StpCost"; // per-port STP path cost

    // Bridge-level STP topology (root election), one row per bridge (e.g. "br-lan").
    // Key dimension: bridge name. Reported by any source that exposes bridge/STP state
    // (OnHub brctl showstp; SNMP BRIDGE-MIB dot1dStp* scalars) — same shape either way.
    public const string BridgeStpEnabled = "Device[].Bridge[].StpEnabled";
    public const string BridgeId = "Device[].Bridge[].Id"; // this bridge's own STP bridge ID
    public const string BridgeRootId = "Device[].Bridge[].RootId"; // designated root bridge ID for this bridge
    public const string BridgeRootPathCost = "Device[].Bridge[].RootPathCost";
    public const string BridgeRootPort = "Device[].Bridge[].RootPort"; // local port toward the root, if not root itself

    // ── GPU (from GpuCollector) ───────────────────────────────────────────────
    // Key = 0-based GPU index as string
    public const string GpuName = "Device[].GPU[].Name";
    public const string GpuVendor = "Device[].GPU[].Vendor";
    public const string GpuVramMB = "Device[].GPU[].VramMB";
    public const string GpuDriverVersion = "Device[].GPU[].DriverVersion";

    // ── Installed packages (from PackageCollector) ────────────────────────────
    // Key = package name (lowercase). Capped at 2000 packages per device.
    public const string PackageVersion = "Device[].Package[].Version";
    public const string PackageManager = "Device[].Package[].Manager"; // "dpkg" | "rpm" | "pacman" | "brew" | "winget"

    // ── Reboot history (from RebootHistoryCollector) ──────────────────────────
    public const string RebootsLastBoot = "Device[].Reboots.LastBoot";

    public const string RebootsCount30d = "Device[].Reboots.Count30d";

    // Key = ordinal (0 = most recent). Up to 20 entries.
    // NOTE: "Boot" must be a leading list dimension — no scalar prefix before it,
    // or ComputeDimKey stops at Device and the fact can't route to proj_reboots_history.
    public const string RebootBootTime = "Device[].Boot[].Time";

    // ── DHCP leases — local file (from DhcpLeaseCollector) ───────────────────
    // Key = MAC address. Reads dnsmasq/dhcpd/kea/OpenWrt lease files directly.
    // Distinct from Service[].DHCP.Scope[].Lease[] which is populated by API.
    // NOTE: "Lease" must be a leading list dimension — no scalar prefix before it,
    // or ComputeDimKey stops at Device and the fact can't route to proj_dhcp_local_leases.
    public const string DhcpLocalLeaseIP = "Device[].Lease[].IP";
    public const string DhcpLocalLeaseHostname = "Device[].Lease[].Hostname";
    public const string DhcpLocalLeaseExpires = "Device[].Lease[].Expires";
    public const string DhcpLocalLeaseSource = "Device[].Lease[].Source"; // "dnsmasq" | "isc-dhcpd" | "kea" | "openwrt"

    // ── SNMP device info (from SnmpDeviceCollector) ───────────────────────────
    public const string SnmpSysDescr = "Device[].SNMP.SysDescr";
    public const string SnmpSysName = "Device[].SNMP.SysName";
    public const string SnmpSysLocation = "Device[].SNMP.SysLocation";
    public const string SnmpSysContact = "Device[].SNMP.SysContact";
    public const string SnmpSysObjectID = "Device[].SNMP.SysObjectID";
    public const string SnmpEngineId = "Device[].SNMP.EngineId";

    // ── BACnet device info (from BacnetDeviceCollector) ──────────────────────
    public const string BacnetDeviceInstance = "Device[].BACnet.DeviceInstance";
    public const string BacnetVendorName = "Device[].BACnet.VendorName";
    public const string BacnetVendorId = "Device[].BACnet.VendorId";
    public const string BacnetModelName = "Device[].BACnet.ModelName";
    public const string BacnetObjectName = "Device[].BACnet.ObjectName";
    public const string BacnetFirmwareRevision = "Device[].BACnet.FirmwareRevision";
    public const string BacnetApplicationSoftwareVersion = "Device[].BACnet.ApplicationSoftwareVersion";
    public const string BacnetDescription = "Device[].BACnet.Description";
    public const string BacnetLocation = "Device[].BACnet.Location";
    public const string BacnetSystemStatus = "Device[].BACnet.SystemStatus";
    public const string BacnetSerialNumber = "Device[].BACnet.SerialNumber";

    // ── Modbus device identity (from ModbusDeviceCollector via FC 43 MEI Type 14) ──
    public const string ModbusVendorName = "Device[].Modbus.VendorName";
    public const string ModbusProductCode = "Device[].Modbus.ProductCode";
    public const string ModbusRevision = "Device[].Modbus.Revision";

    // ── Modbus device registers (from ModbusDeviceCollector) ─────────────────
    // Key = register address (decimal string, e.g. "0", "1", "40001").
    public const string ModbusHoldingRegister = "Device[].Modbus.HoldingRegister[]";
    public const string ModbusInputRegister = "Device[].Modbus.InputRegister[]";
    public const string ModbusCoil = "Device[].Modbus.Coil[]";

    // ── Network discovery (from NetworkDiscoveryCollector) ────────────────────
    // Key = IP address of the discovered neighbor.
    // Well-known scanner attributes are emitted as direct sibling paths below so
    // the projection system can route them. Truly unknown Attr[{key}] sub-keys are
    // stored in the raw facts table only.
    public const string DiscoveredMAC = "Device[].Discovered[].MAC";
    public const string DiscoveredHostname = "Device[].Discovered[].Hostname";

    // Obscured MAC (Google Wifi masks the last nibble with '*'). Kept as a raw
    // observation; never a device fingerprint. The server reconstructs it against
    // the known-MAC set and, on a unique match, populates DiscoveredMAC.
    public const string DiscoveredObscuredMAC = "Device[].Discovered[].ObscuredMAC";

    public const string DiscoveredSources = "Device[].Discovered[].Sources"; // comma-separated scanner names

    // Hardware identity / fingerprint candidates
    public const string DiscoveredOnvifSerial = "Device[].Discovered[].OnvifSerial";
    public const string DiscoveredRokuSerial = "Device[].Discovered[].RokuSerial";
    public const string DiscoveredSsdpUuid = "Device[].Discovered[].SsdpUuid";

    public const string DiscoveredWsdUuid = "Device[].Discovered[].WsdUuid";

    // SSH host-key fingerprint ("sha256:<base64>"), a stable per-host identity.
    public const string DiscoveredSshHostKey = "Device[].Discovered[].SshHostKey";

    // ── Intrinsic device attributes ───────────────────────────────────────────
    // Properties OF the observed device (not of the observation). On reconstruction
    // the server PROMOTES these onto the resolved Device[]'s canonical projections
    // (proj_systems/proj_hardware/proj_devices), mirroring the Device[] schema:
    //   Discovered[].Hostname     → Device[].OS.Hostname
    //   Discovered[].Model        → Device[].Hardware.SystemModel
    //   Discovered[].DeviceType   → Device[].Kind
    public const string DiscoveredVendor = "Device[].Discovered[].Vendor";
    public const string DiscoveredModel = "Device[].Discovered[].Model";

    public const string DiscoveredFirmware = "Device[].Discovered[].Firmware";

    // mDNS-derived identity (Google Wifi + mDNS scanner).
    public const string DiscoveredFriendlyName = "Device[].Discovered[].FriendlyName"; // mDNS fn=
    public const string DiscoveredDeviceType = "Device[].Discovered[].DeviceType"; // e.g. "Nest-Audio", "Chromecast", "OnHub Mesh Point"

    // Stable Google Cast device id (32 hex) from the _googlecast mDNS advertisement.
    // Anchors mDNS identity to the device, decoupled from IP (survives DHCP churn),
    // so a stale advertisement on a reused IP can't smear onto the new occupant.
    public const string DiscoveredCastId = "Device[].Discovered[].CastId";

    // Raw mDNS "ca=" value from the _googlecast TXT record — a capability bitmask. Captured
    // opaque/unparsed: Google never published the bit layout, and reverse-engineered write-ups
    // disagree on the details, so we keep the raw value rather than decode a guessed meaning.
    public const string DiscoveredCastCapabilities = "Device[].Discovered[].CastCapabilities";

    // Advertised services — genuinely multi-valued, so a list dimension (key =
    // service type, e.g. "_googlecast._tcp"). Mirrors a device's service catalogue.
    // The .Name leaf holds the service type (a named attribute is required so it
    // doesn't collide with the "Service" dimension-key column).
    public const string DiscoveredServiceName = "Device[].Discovered[].Service[].Name";

    // Whether the router (Google Wifi/OnHub) has an explicit DHCP reservation for this
    // device — a stable device property (the owner configured it), not a per-sighting
    // metric, so it lives here rather than under Link.*.
    public const string DiscoveredIsDhcpReserved = "Device[].Discovered[].IsDhcpReserved";

    // The IPv4 address reserved for this device in the router's DHCP reservation table
    // (dhcp_reservation.ip_address). Informational/display only — the OnHub cannot report
    // true DHCP lease data (no expiry/renewal anywhere in the diagnostic report), so this is
    // display-only, not used for identity resolution or cross-device joins.
    public const string DiscoveredDhcpReservedIp = "Device[].Discovered[].DhcpReservedIp";

    // ── The sighting / link (observer↔neighbor edge) ─────────────────────────
    // NOT device properties — one observer's view at one moment (RSSI differs per
    // observer). These STAY on the observation; they are never promoted to Device[].
    public const string DiscoveredLinkMedium = "Device[].Discovered[].Link.Medium"; // "wired" | "wireless"
    public const string DiscoveredLinkBand = "Device[].Discovered[].Link.Band"; // "2.4GHz" | "5GHz"
    public const string DiscoveredLinkGuest = "Device[].Discovered[].Link.Guest"; // guest-network membership
    public const string DiscoveredLinkSignalDbm = "Device[].Discovered[].Link.SignalDbm"; // RSSI, negative dBm
    public const string DiscoveredLinkTxRateMbps = "Device[].Discovered[].Link.TxRateMbps";
    public const string DiscoveredLinkRxRateMbps = "Device[].Discovered[].Link.RxRateMbps";
    public const string DiscoveredLinkRxBytes = "Device[].Discovered[].Link.RxBytes";
    public const string DiscoveredLinkTxBytes = "Device[].Discovered[].Link.TxBytes";
    public const string DiscoveredLinkConnectedSeconds = "Device[].Discovered[].Link.ConnectedSeconds";

    // Which physical Google Wifi/OnHub mesh point currently relays this client (real,
    // unobscured bssid — resolved via the mesh routing table + mesh_node_info). A link
    // property (which AP a client currently associates through), not a device property.
    public const string DiscoveredLinkMeshApBssid = "Device[].Discovered[].Link.MeshApBssid";

    // hostapd's own STA-activity + IAPP roaming log (/var/log/messages) — the only
    // *temporal* signal in the Google Wifi/OnHub report; everything else above is a
    // single point-in-time snapshot. Link-scoped like the rest of this group.
    public const string DiscoveredLinkLastActiveAt = "Device[].Discovered[].Link.LastActiveAt";
    public const string DiscoveredLinkLastActiveInterface = "Device[].Discovered[].Link.LastActiveInterface";
    public const string DiscoveredLinkLastRoamingAt = "Device[].Discovered[].Link.LastRoamingAt";
    public const string DiscoveredLinkLastRoamingApIp = "Device[].Discovered[].Link.LastRoamingApIp";

    // Raw mDNS "st="/"rs=" values from the _googlecast TXT record — transient state, not a
    // stable device property (what's currently running changes constantly), so these live on
    // the sighting like the other Link.* facts rather than being promoted to Device[]. Captured
    // opaque/unparsed for the same reason as CastCapabilities above.
    public const string DiscoveredLinkCastStatus = "Device[].Discovered[].Link.CastStatus"; // mDNS st=
    public const string DiscoveredLinkCastRunningApp = "Device[].Discovered[].Link.CastRunningApp"; // mDNS rs=

    // Additional identity context
    public const string DiscoveredTlsCn = "Device[].Discovered[].TlsCn";
    public const string DiscoveredHttpTitle = "Device[].Discovered[].HttpTitle";
    public const string DiscoveredHttpServer = "Device[].Discovered[].HttpServer";
    public const string DiscoveredSmb2Dialect = "Device[].Discovered[].Smb2Dialect";
    public const string DiscoveredLdapNamingContext = "Device[].Discovered[].LdapNamingContext";
    public const string DiscoveredUpnpDeviceType = "Device[].Discovered[].UpnpDeviceType";

    public const string DiscoveredPresentationUrl = "Device[].Discovered[].PresentationUrl";

    // Scanner identity signals promoted to fingerprints (help merge a device across observers).
    public const string DiscoveredHueBridgeId = "Device[].Discovered[].HueBridgeId";
    public const string DiscoveredOnvifHardwareId = "Device[].Discovered[].OnvifHardwareId";

    // ── Scanner protocol signals (formerly raw Attr[key] facts; now fully typed) ──────────────
    // TLS certificate (from TlsCertScanner) — subject/issuer/serial/expiry read off the presented cert.
    public const string DiscoveredTlsSubject = "Device[].Discovered[].TlsSubject";
    public const string DiscoveredTlsIssuer = "Device[].Discovered[].TlsIssuer";
    public const string DiscoveredTlsSerial = "Device[].Discovered[].TlsSerial";
    public const string DiscoveredTlsNotAfter = "Device[].Discovered[].TlsNotAfter"; // cert expiry (DateTimeOffset)

    // SSH (from SshBannerScanner) — the raw identification banner (carries version).
    public const string DiscoveredSshBanner = "Device[].Discovered[].SshBanner";

    // LDAP (from LdapScanner) — the directory server's own serverName DN.
    public const string DiscoveredLdapServerName = "Device[].Discovered[].LdapServerName";

    // BACnet (from BacnetScanner) — device identity from the I-Am reply.
    public const string DiscoveredBacnetInstance = "Device[].Discovered[].BacnetInstance"; // Long
    public const string DiscoveredBacnetVendorId = "Device[].Discovered[].BacnetVendorId"; // Long

    // HTTP (from HttpBannerScanner) — probe result diagnostics.
    public const string DiscoveredHttpStatus = "Device[].Discovered[].HttpStatus"; // Long
    public const string DiscoveredHttpUrl = "Device[].Discovered[].HttpUrl";

    // Favicon hashes (from HttpBannerScanner) — firmware-baked asset used as a device fingerprint.
    public const string DiscoveredFaviconMd5 = "Device[].Discovered[].FaviconMd5"; // MD5 hex (Recog favicons.xml)
    public const string DiscoveredFaviconMmh3 = "Device[].Discovered[].FaviconMmh3"; // Long (Shodan mmh3, signed 32-bit)

    // HTTP identity resolved on-agent from Recog fingerprint matching (from HttpBannerScanner).
    public const string DiscoveredOs = "Device[].Discovered[].Os"; // OS product/family hint
    public const string DiscoveredHttpConfidence = "Device[].Discovered[].HttpConfidence"; // Double 0..1
    public const string DiscoveredHttpIdentitySource = "Device[].Discovered[].HttpIdentitySource"; // field:signal provenance
    public const string DiscoveredHttpSerial = "Device[].Discovered[].HttpSerial"; // serial from an HTTP follow-up (e.g. UPnP)
    public const string DiscoveredSnmpSerial = "Device[].Discovered[].SnmpSerial"; // Printer-MIB prtGeneralSerialNumber

    // Printer status/consumables/hardware detail (from HttpBannerScanner's HP LEDM / Epson EWS
    // follow-ups — see PrinterFollowUps.FetchHpAsync/FetchEpsonAsync). Pipe-joined where a printer
    // reports more than one value (multiple status categories, alerts, or ink/toner supplies).
    public const string DiscoveredPrinterStatus = "Device[].Discovered[].PrinterStatus";
    public const string DiscoveredPrinterAlerts = "Device[].Discovered[].PrinterAlerts"; // "severity:id|..." (non-Info only)
    public const string DiscoveredPrinterConsumables = "Device[].Discovered[].PrinterConsumables"; // "label:level|..."
    public const string DiscoveredPrinterProductNumber = "Device[].Discovered[].PrinterProductNumber"; // HP SKU, e.g. "T0F29A"

    // SSDP (from SsdpScanner) — advertisement headers.
    public const string DiscoveredSsdpServer = "Device[].Discovered[].SsdpServer";
    public const string DiscoveredSsdpSt = "Device[].Discovered[].SsdpSt"; // search target

    // RTSP (from RtspScanner).
    public const string DiscoveredRtspServer = "Device[].Discovered[].RtspServer";
    public const string DiscoveredRtspContentType = "Device[].Discovered[].RtspContentType";
    public const string DiscoveredRtspMethods = "Device[].Discovered[].RtspMethods"; // comma-joined verb list
    public const string DiscoveredRtspPort = "Device[].Discovered[].RtspPort"; // Long

    // MQTT (from MqttScanner).
    public const string DiscoveredMqttAuthRequired = "Device[].Discovered[].MqttAuthRequired"; // Bool
    public const string DiscoveredMqttReturnCode = "Device[].Discovered[].MqttReturnCode"; // CONNACK code (hex text)
    public const string DiscoveredMqttPort = "Device[].Discovered[].MqttPort"; // Long

    // Modbus (from ModbusScanner).
    public const string DiscoveredModbusUnitId = "Device[].Discovered[].ModbusUnitId"; // Long
    public const string DiscoveredModbusPort = "Device[].Discovered[].ModbusPort"; // Long

    // Philips Hue (from PhilipsHueScanner).
    public const string DiscoveredHueApiVersion = "Device[].Discovered[].HueApiVersion";

    // Google Cast / Eureka (from EurekaScanner).
    public const string DiscoveredEurekaCastVersion = "Device[].Discovered[].EurekaCastVersion";
    public const string DiscoveredEurekaSsid = "Device[].Discovered[].EurekaSsid"; // Wi-Fi SSID the device is on

    // IPP printers (from IppScanner).
    public const string DiscoveredIppLocation = "Device[].Discovered[].IppLocation";

    // AirPlay (from AirPlayScanner).
    public const string DiscoveredAirplayFeatures = "Device[].Discovered[].AirplayFeatures"; // capability bitmask (hex)
    public const string DiscoveredAirplayPlistFormat = "Device[].Discovered[].AirplayPlistFormat";

    // WS-Discovery (from WsDiscoveryScanner).
    public const string DiscoveredWsdTypes = "Device[].Discovered[].WsdTypes"; // comma-joined QName list
    public const string DiscoveredWsdMetadataVersion = "Device[].Discovered[].WsdMetadataVersion";

    // ONVIF (from OnvifScanner) — whether the device required auth for the probe.
    public const string DiscoveredOnvifAuthRequired = "Device[].Discovered[].OnvifAuthRequired"; // Bool

    // NBNS response header (from NbnsScanner) — scalar per Discovered IP, RFC 1002 §4.2.1.1.
    public const string DiscoveredNbnsOpCode = "Device[].Discovered[].NbnsOpCode";
    public const string DiscoveredNbnsResultCode = "Device[].Discovered[].NbnsResultCode";
    public const string DiscoveredNbnsAuthoritative = "Device[].Discovered[].NbnsAuthoritative"; // Bool
    public const string DiscoveredNbnsTruncated = "Device[].Discovered[].NbnsTruncated"; // Bool
    public const string DiscoveredNbnsBroadcast = "Device[].Discovered[].NbnsBroadcast"; // Bool
    public const string DiscoveredNbnsRecursionDesired = "Device[].Discovered[].NbnsRecursionDesired"; // Bool
    public const string DiscoveredNbnsRecursionAvailable = "Device[].Discovered[].NbnsRecursionAvailable"; // Bool

    // ── Multi-valued scanner signals modelled as list sub-dimensions (key = the item) ─────────
    public const string DiscoveredNbnsName = "Device[].Discovered[].NbnsName[].Name"; // one row per NetBIOS name

    // NBNS per-name detail (from NbnsScanner) — sibling attributes on the same NbnsName[] key,
    // RFC 1002 §4.2.18.
    public const string DiscoveredNbnsSuffix = "Device[].Discovered[].NbnsName[].Suffix"; // Long (raw byte)
    public const string DiscoveredNbnsSuffixDescription = "Device[].Discovered[].NbnsName[].SuffixDescription";
    public const string DiscoveredNbnsOwnerNodeType = "Device[].Discovered[].NbnsName[].OwnerNodeType";
    public const string DiscoveredNbnsIsGroup = "Device[].Discovered[].NbnsName[].IsGroup"; // Bool
    public const string DiscoveredNbnsIsPermanent = "Device[].Discovered[].NbnsName[].IsPermanent"; // Bool
    public const string DiscoveredNbnsIsActive = "Device[].Discovered[].NbnsName[].IsActive"; // Bool
    public const string DiscoveredNbnsIsInConflict = "Device[].Discovered[].NbnsName[].IsInConflict"; // Bool
    public const string DiscoveredNbnsIsBeingDeregistered = "Device[].Discovered[].NbnsName[].IsBeingDeregistered"; // Bool

    public const string DiscoveredCoapResource = "Device[].Discovered[].CoapResource[].Path"; // one row per CoAP resource
    public const string DiscoveredCoapContentFormat = "Device[].Discovered[].CoapContentFormat[].Id"; // ct= formats

    // ── SSH device raw facts (from SshDeviceCollector) ───────────────────────
    // Emitted for devices that don't run the agent natively. Values are
    // human-readable strings rather than typed integers.
    public const string SshInterfaceIP = "Device[].Interface[].IP"; // IP without prefix length
    public const string SshCpuCount = "Device[].CPU.Count"; // nproc output
    public const string SshMemTotalMB = "Device[].Memory.TotalMB"; // from /proc/meminfo
    public const string SshMemUsedMB = "Device[].Memory.UsedMB"; // from /proc/meminfo
    public const string SshFsSize = "Device[].Filesystem[].Size"; // df -h (e.g. "50G")
    public const string SshFsUsed = "Device[].Filesystem[].Used"; // df -h (e.g. "30G")
    public const string SshFsUsePercent = "Device[].Filesystem[].UsePercent"; // df -h (e.g. "60%")

    // ── Derived ───────────────────────────────────────────────────────────────
    // Computed by the analysis pipeline. A fact may be observed on one device
    // (Go app computes it directly) and derived on another — same path, same
    // semantics. The last value wins in the projection table.
    public static class Derived
    {
        public const string SystemMemUsedPercent = "Device[].System.MemUsedPercent";
        public const string InterfaceTotalBytes = "Device[].Interface[].TotalBytes";
        public const string FsUsedPercent = "Device[].Filesystem[].UsedPercent";
        public const string BatteryHealthPercent = "Device[].Battery.HealthPercent";

        // Fanned in from whichever protocol collector reports a vendor for this device
        // (DeviceVendorDerivation) — the one place a cross-device report should read "vendor".
        public const string DeviceVendorCanonical = "Device[].VendorCanonical";

        // Fanned in from whichever raw model field is present (DeviceModelDerivation), with a
        // vendor+OS-dispatched cleanup pass applied on top — turns a raw SKU string (e.g.
        // "WS-C9300-48P") into a clean product-family display name (e.g. "Catalyst 9300"). Kept
        // separate from the raw HwSystemModel/DiscoveredModel/BacnetModelName paths (fan-in
        // precedent, same as DeviceVendorCanonical) rather than overwriting any one of them —
        // there's no single canonical raw model path the way there is for vendor.
        public const string DeviceModelCanonical = "Device[].ModelCanonical";

        // Inferred vendor guess from a curated, vendor-exclusive signature (OS distro,
        // SNMP sysDescr, model prefix, hostname prefix — see docs/plans/vendor-derivation-updates.md
        // §2). Deliberately kept separate from DeviceVendorCanonical: that field is a fan-in over
        // protocols that self-report vendor directly, this one is inferred from a proxy signal.
        // Reporting should only consult this when DeviceVendorCanonical is empty.
        public const string DeviceVendorGuess = "Device[].VendorGuess";

        // Inferred OS-distro guess from a curated, OS-exclusive signature (SNMP sysDescr today —
        // see docs/plans/vendor-derivation-updates.md §5). Kept separate from the raw
        // Device[].OS.Distro fact path rather than appended as another writer of it: that path is
        // projected via the plain last-write-wins GenericProjection route, so a guess re-derived
        // every collection cycle (fresh collected_at each time) could silently outrace and clobber
        // an older, more authoritative device-reported value. Reporting should only consult this
        // when Device[].OS.Distro is empty.
        public const string DeviceOsGuess = "Device[].OsGuess";
    }

    // ── Metric classification ──────────────────────────────────────────────────
    // Monotonic counters that differ on nearly every poll by construction — dedup-on-write
    // against facts_history (LATERAL LIMIT 1 per fact id) is pure write amplification for
    // these, not an optimization. Routed to metrics_raw instead: unconditional insert,
    // range-partitioned, short retention. See docs/plans/metrics-retention.md.
    public static readonly IReadOnlySet<string> MetricPaths = new HashSet<string>
    {
        InterfaceRxBytes,
        InterfaceTxBytes,
        InterfaceRxPackets,
        InterfaceTxPackets,
        Derived.InterfaceTotalBytes,
        DiscoveredLinkRxBytes,
        DiscoveredLinkTxBytes,
    };
}

// ── Logical services ──────────────────────────────────────────────────────────
//
// Facts about logical services — entities that run on a device but have an
// identity independent of the underlying hardware (survives host migration).
//
// The top-level dimension is Service[] keyed by the stable ServiceId assigned
// by the server's ServiceRegistry. Sub-namespaces (DNS, DHCP, VPN, ...) hold
// capability-specific facts so the same entity can represent a combined service
// like Technitium (which is both a DNS and DHCP server).
//
// Service[{id}].DeviceId links back to the Device[] record for the host that
// currently runs the service, enabling cross-dimension joins.

public static class ServicePaths
{
    // ── Identity ──────────────────────────────────────────────────────────────
    /// <summary>ServiceId of this service — self-referential, useful for joins.</summary>
    public const string ServiceId = "Service[].ServiceId";

    /// <summary>Stable DeviceId of the host currently running this service.</summary>
    public const string DeviceId = "Service[].DeviceId";

    /// <summary>Service type slug. e.g. "technitium-dns", "adguard-home", "pihole"</summary>
    public const string Type = "Service[].Type";

    // ── DNS capability ────────────────────────────────────────────────────────
    // Populated by any service that provides DNS resolution or authoritative zones.

    public const string DnsStatsTotalQueries = "Service[].DNS.Stats.TotalQueries";
    public const string DnsStatsTotalBlocked = "Service[].DNS.Stats.TotalBlocked";
    public const string DnsStatsBlockedPct = "Service[].DNS.Stats.BlockedPct";

    // Top-N lists — second key dimension is domain name or client IP
    public const string DnsTopQueried = "Service[].DNS.TopQueried[].Hits";
    public const string DnsTopBlocked = "Service[].DNS.TopBlocked[].Hits";
    public const string DnsTopClients = "Service[].DNS.TopClients[].Hits";

    // Authoritative zones — second key dimension is zone name
    public const string DnsZoneType = "Service[].DNS.Zone[].Type";

    // Zone records — key dimensions: zone name, hostname, record type (A/AAAA/CNAME).
    // Record type is a key dimension so A and AAAA for the same hostname don't
    // collide, and CNAME (whose value is a target name, not an IP) coexists too.
    public const string DnsZoneRecordIP = "Service[].DNS.Zone[].Record[].RType[].IP"; // A / AAAA
    public const string DnsZoneRecordTarget = "Service[].DNS.Zone[].Record[].RType[].Target"; // CNAME target
    public const string DnsZoneRecordTTL = "Service[].DNS.Zone[].Record[].RType[].TTL";

    // ── CA capability ─────────────────────────────────────────────────────────
    // Populated by certificate authority services (step-ca, etc.).

    public const string CaStatus = "Service[].CA.Status"; // "running" | "stopped"
    public const string CaAddress = "Service[].CA.Address"; // e.g. ":9000"
    public const string CaDnsName = "Service[].CA.DnsName[].Value"; // DNS SANs the CA is known by
    public const string CaRootSubjectDn = "Service[].CA.Root.SubjectDn";
    public const string CaRootNotBefore = "Service[].CA.Root.NotBefore";
    public const string CaRootNotAfter = "Service[].CA.Root.NotAfter";
    public const string CaRootFingerprint = "Service[].CA.Root.Fingerprint";
    public const string CaIntermediateSubjectDn = "Service[].CA.Intermediate.SubjectDn";
    public const string CaIntermediateNotBefore = "Service[].CA.Intermediate.NotBefore";

    public const string CaIntermediateNotAfter = "Service[].CA.Intermediate.NotAfter";

    // Provisioners — key dimension is provisioner name
    public const string CaProvisionerType = "Service[].CA.Provisioner[].Type";
    public const string CaProvisionerDefaultDuration = "Service[].CA.Provisioner[].DefaultDuration";

    // ── DHCP capability ───────────────────────────────────────────────────────
    // Populated by any service that manages DHCP address assignments.

    // Scopes — key dimension is scope name
    public const string DhcpScopeEnabled = "Service[].DHCP.Scope[].Enabled";
    public const string DhcpScopeStartAddress = "Service[].DHCP.Scope[].StartAddress";
    public const string DhcpScopeEndAddress = "Service[].DHCP.Scope[].EndAddress";
    public const string DhcpScopeSubnetMask = "Service[].DHCP.Scope[].SubnetMask";
    public const string DhcpScopeGateway = "Service[].DHCP.Scope[].Gateway";

    // Leases — key dimensions: scope name, MAC address.
    // Nesting under Scope preserves context and handles the case where the
    // same MAC might appear in scopes on different servers.
    public const string DhcpLeaseIP = "Service[].DHCP.Scope[].Lease[].IP";
    public const string DhcpLeaseHostname = "Service[].DHCP.Scope[].Lease[].Hostname";
    public const string DhcpLeaseType = "Service[].DHCP.Scope[].Lease[].Type"; // Dynamic, Reserved, Static
    public const string DhcpLeaseExpires = "Service[].DHCP.Scope[].Lease[].Expires";

    // ── Home Assistant capability ─────────────────────────────────────────────
    // Populated by HomeAssistantCollector. ServiceType = "home-assistant".

    public const string HomeAssistantDeviceId = "Service[].HomeAssistant.DeviceId";
    public const string HomeAssistantSupervisorVersion = "Service[].HomeAssistant.SupervisorVersion";
    public const string HomeAssistantCoreVersion = "Service[].HomeAssistant.CoreVersion";
    public const string HomeAssistantOsVersion = "Service[].HomeAssistant.OsVersion";
    public const string HomeAssistantOsBoard = "Service[].HomeAssistant.OsBoard";
    public const string HomeAssistantChannel = "Service[].HomeAssistant.Channel"; // "stable" | "beta" | "dev"
    public const string HomeAssistantHostname = "Service[].HomeAssistant.Hostname";

    // Add-ons — key dimension is the add-on slug (e.g. "core_mosquitto")
    public const string HomeAssistantAddOnName = "Service[].HomeAssistant.AddOn[].Name";
    public const string HomeAssistantAddOnVersion = "Service[].HomeAssistant.AddOn[].Version";

    public const string
        HomeAssistantAddOnState = "Service[].HomeAssistant.AddOn[].State"; // "started" | "stopped" | ...

    public const string HomeAssistantAddOnUpdateAvailable = "Service[].HomeAssistant.AddOn[].UpdateAvailable";

    // Device-registry inventory — key dimension is the HA device-registry entry's own
    // stable id (a UUID minted by HA, stable across restarts). Populated by
    // HomeAssistantDeviceCollector. ServiceType = "home-assistant-devices" — a distinct
    // service target from the Supervisor-facing "home-assistant" collector above, since it
    // hits HA Core (not Supervisor) with a separate user token.
    // HomeAssistantDevicePromotion resolves each HaDevice[] entry into its own Device[] row
    // inline during ingest (correlated by Mac when present, else a FingerprintType.HaIdentifiers
    // fingerprint built from Identifiers) — not DiscoveryMaterializer; see
    // docs/plans/ha-inline-discovery.md. Only Manufacturer/Model/Name are promoted onto the
    // resolved device today; the rest stay queryable here.
    public const string HomeAssistantHaDeviceMac = "Service[].HomeAssistant.HaDevice[].Mac";

    // The bare UUID(s) out of this device's "upnp"-flavored connections/identifiers
    // ("uuid:xxxx-..." or the fuller "uuid:xxxx-...::urn:schemas-upnp-org:device:...") — the
    // same UUID a network-scanner's SSDP probe would observe independently (see
    // NetworkDiscoveryCollector.EmitSsdpUuid). A device can carry more than one (e.g. separate
    // IGD/WPS root UPnP devices on one router); pipe-joined when there's more than one.
    // Promoted as one FingerprintType.Uuid fingerprint per value — see
    // HomeAssistantDeviceCollector.ExtractUpnpUuids — rather than folded into the opaque
    // Identifiers blob below, letting HA-reported and SSDP-scanned observations of the same
    // device merge onto one Device row.
    public const string HomeAssistantHaDeviceUpnpUuid = "Service[].HomeAssistant.HaDevice[].UpnpUuid";
    public const string HomeAssistantHaDeviceIdentifiers = "Service[].HomeAssistant.HaDevice[].Identifiers";
    public const string HomeAssistantHaDeviceManufacturer = "Service[].HomeAssistant.HaDevice[].Manufacturer";
    public const string HomeAssistantHaDeviceModel = "Service[].HomeAssistant.HaDevice[].Model";
    public const string HomeAssistantHaDeviceModelId = "Service[].HomeAssistant.HaDevice[].ModelId";
    public const string HomeAssistantHaDeviceHwVersion = "Service[].HomeAssistant.HaDevice[].HwVersion";
    public const string HomeAssistantHaDeviceSwVersion = "Service[].HomeAssistant.HaDevice[].SwVersion";
    public const string HomeAssistantHaDeviceSerialNumber = "Service[].HomeAssistant.HaDevice[].SerialNumber";

    // Pipe-joined when a device carries more than one label.
    public const string HomeAssistantHaDeviceLabels = "Service[].HomeAssistant.HaDevice[].Labels";
    public const string HomeAssistantHaDeviceName = "Service[].HomeAssistant.HaDevice[].Name";
    public const string HomeAssistantHaDeviceAreaName = "Service[].HomeAssistant.HaDevice[].AreaName";

    // The parent coordinator/hub's own HaDevice[] key (Zigbee/Z-Wave devices reference the
    // dongle they're paired through) — informational; not resolved to a DeviceId in v1.
    public const string HomeAssistantHaDeviceViaDeviceKey = "Service[].HomeAssistant.HaDevice[].ViaDeviceKey";

    // Health signals mined from a `connectivity` binary_sensor / `battery` sensor / `update`
    // entity belonging to the device (matched via the entity registry's device_id).
    public const string HomeAssistantHaDeviceOnline = "Service[].HomeAssistant.HaDevice[].Online";
    public const string HomeAssistantHaDeviceBatteryPercent = "Service[].HomeAssistant.HaDevice[].BatteryPercent";
    public const string HomeAssistantHaDeviceUpdateAvailable = "Service[].HomeAssistant.HaDevice[].UpdateAvailable";
    public const string HomeAssistantHaDeviceLatestVersion = "Service[].HomeAssistant.HaDevice[].LatestVersion";
}