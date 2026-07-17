using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Server.FactViews;

/// <summary>
/// The registry of device-detail fact views. Add a view here — no table, migration, or index —
/// to surface a family of device-only facts as a labelled table. Attribute columns reference
/// <see cref="FactPaths" /> constants (typo-safe, grammar-derived). The fact-path routing
/// fitness test treats a path consumed by a view here as having a home, so a view is the
/// low-cost alternative to a projection for data that never needs a cross-device query.
/// Every view declares a <see cref="FactViewGroup" /> so the device-detail section nav can file
/// it under the right group.
/// </summary>
public static class FactViewLibrary
{
    public static readonly IReadOnlyList<FactViewDef> All =
    [
        new(
            "Thermal",
            [
                FactViewColumn.Key("Zone"),
                FactViewColumn.Fact("°C", FactPaths.HwTemperatureCelsius),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Hardware
        ),

        new(
            "Pending Updates",
            [
                FactViewColumn.Fact("Package", FactPaths.UpdatePendingName),
                FactViewColumn.Fact("New Version", FactPaths.UpdatePendingNewVersion),
                FactViewColumn.Fact("Source", FactPaths.UpdatePendingSource),
                FactViewColumn.Fact("Security", FactPaths.UpdatePendingSecurity),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        new(
            "Processes",
            [
                FactViewColumn.Key("PID"),
                FactViewColumn.Fact("Name", FactPaths.ProcessName),
                FactViewColumn.Fact("CPU (s)", FactPaths.ProcessCpuTimeSecs),
                FactViewColumn.Fact("Memory (bytes)", FactPaths.ProcessMemBytes),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        new(
            "Routes",
            [
                FactViewColumn.Key("Destination"),
                FactViewColumn.Fact("Family", FactPaths.RouteFamily),
                FactViewColumn.Fact("Gateway", FactPaths.RouteGateway),
                FactViewColumn.Fact("Interface", FactPaths.RouteInterface),
                FactViewColumn.Fact("Metric", FactPaths.RouteMetric),
                FactViewColumn.Fact("Proto", FactPaths.RouteProto),
                FactViewColumn.Fact("Source", FactPaths.RouteSource),
                FactViewColumn.Fact("Scope", FactPaths.RouteScope),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        new(
            "Users",
            [
                FactViewColumn.Fact("Username", FactPaths.LocalUserUsername),
                FactViewColumn.Fact("UID", FactPaths.LocalUserUid),
                FactViewColumn.Fact("GID", FactPaths.LocalUserGid),
                FactViewColumn.Fact("Home", FactPaths.LocalUserHome),
                FactViewColumn.Fact("Shell", FactPaths.LocalUserShell),
                FactViewColumn.Fact("Admin", FactPaths.LocalUserIsAdmin),
                FactViewColumn.Fact("Disabled", FactPaths.LocalUserDisabled),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        new(
            "Active Sessions",
            [
                FactViewColumn.Fact("User", FactPaths.SessionUser),
                FactViewColumn.Fact("TTY", FactPaths.SessionTty),
                FactViewColumn.Fact("Login", FactPaths.SessionLoginAt),
                FactViewColumn.Fact("From", FactPaths.SessionHost),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        new(
            "System Services",
            [
                FactViewColumn.Fact("Unit", FactPaths.ServiceName),
                FactViewColumn.Fact("Display Name", FactPaths.ServiceDisplayName),
                FactViewColumn.Fact("Active", FactPaths.ServiceActiveState),
                FactViewColumn.Fact("Sub-state", FactPaths.ServiceSubState),
                FactViewColumn.Fact("Exit Code", FactPaths.ServiceExitCode),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        // Moved off proj_systems (migration 0060): single-device display only, no cross-device
        // query need (hostname/os_family/os_distro stay projected for that).
        new(
            "OS Details",
            [
                FactViewColumn.Fact("Version", FactPaths.SystemOsVersion),
                FactViewColumn.Fact("Build", FactPaths.SystemOsBuild),
                FactViewColumn.Fact("Kernel", FactPaths.SystemKernel),
                FactViewColumn.Fact("Kernel arch", FactPaths.SystemKernelArch),
                FactViewColumn.Fact("Timezone", FactPaths.SystemTimezone),
                FactViewColumn.Fact("Boot time", FactPaths.SystemBootTime),
                FactViewColumn.Fact("Uptime (s)", FactPaths.SystemUptimeSeconds),
            ],
            FactViewKind.Properties,
            FactViewGroup.Software
        ),

        // Live performance metrics — moved off proj_systems (migration 0060): rewritten on
        // every collection cycle, read by nothing (not even this page) before the move.
        new(
            "Resource Usage",
            [
                FactViewColumn.Fact("CPU %", FactPaths.SystemCpuPercent),
                FactViewColumn.Fact("Memory used", FactPaths.SystemMemUsedBytes),
                FactViewColumn.Fact("Memory total", FactPaths.SystemMemTotalBytes),
                FactViewColumn.Fact("Memory used %", FactPaths.Derived.SystemMemUsedPercent),
                FactViewColumn.Fact("Load (1m)", FactPaths.SystemLoad1),
                FactViewColumn.Fact("Load (5m)", FactPaths.SystemLoad5),
                FactViewColumn.Fact("Load (15m)", FactPaths.SystemLoad15),
            ],
            FactViewKind.Properties,
            FactViewGroup.Hardware
        ),

        new(
            "GPU",
            [
                FactViewColumn.Fact("Name", FactPaths.GpuName),
                FactViewColumn.Fact("Vendor", FactPaths.GpuVendor),
                FactViewColumn.Fact("VRAM (MB)", FactPaths.GpuVramMB),
                FactViewColumn.Fact("Driver", FactPaths.GpuDriverVersion),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Hardware
        ),

        new(
            "Battery",
            [
                FactViewColumn.Fact("State", FactPaths.BatteryState),
                FactViewColumn.Fact("Charge %", FactPaths.BatteryChargePercent),
                FactViewColumn.Fact("Health %", FactPaths.Derived.BatteryHealthPercent),
                FactViewColumn.Fact("Design capacity (Wh)", FactPaths.BatteryDesignCapWh),
                FactViewColumn.Fact("Current capacity (Wh)", FactPaths.BatteryCurrentCapWh),
                FactViewColumn.Fact("Cycle count", FactPaths.BatteryCycleCount),
            ],
            FactViewKind.Properties,
            FactViewGroup.Hardware
        ),

        new(
            "Certificates",
            [
                FactViewColumn.Fact("Subject", FactPaths.CertSubjectDn),
                FactViewColumn.Fact("Issuer", FactPaths.CertIssuerDn),
                FactViewColumn.Fact("Not Before", FactPaths.CertNotBefore),
                FactViewColumn.Fact("Not After", FactPaths.CertNotAfter),
                FactViewColumn.Fact("Path", FactPaths.CertPath),
                FactViewColumn.Fact("CA", FactPaths.CertIsCA),
                FactViewColumn.Fact("SANs", FactPaths.CertSANs),
                FactViewColumn.Fact("Serial", FactPaths.CertSerial),
                FactViewColumn.Fact("Signature Algorithm", FactPaths.CertSigAlgo),
                FactViewColumn.Fact("Key Size", FactPaths.CertKeySize),
                FactViewColumn.Fact("Key Usage", FactPaths.CertKeyUsage),
                FactViewColumn.Fact("EKU", FactPaths.CertEku),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        new(
            "Trusted CAs",
            [
                FactViewColumn.Fact("CA URL", FactPaths.TrustedCaCaUrl),
                FactViewColumn.Fact("Root Path", FactPaths.TrustedCaRootPath),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        new(
            "Software Updates",
            [
                FactViewColumn.Fact("Manager", FactPaths.UpdateManager),
                FactViewColumn.Fact("Pending", FactPaths.UpdatePendingCount),
                FactViewColumn.Fact("Security", FactPaths.UpdateSecurityCount),
                FactViewColumn.Fact("Reboot required", FactPaths.UpdateRebootRequired),
            ],
            FactViewKind.Properties,
            FactViewGroup.Software
        ),

        new(
            "Packages",
            [
                FactViewColumn.Key("Package"),
                FactViewColumn.Fact("Version", FactPaths.PackageVersion),
                FactViewColumn.Fact("Manager", FactPaths.PackageManager),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        new(
            "Reboots",
            [
                FactViewColumn.Fact("Last boot", FactPaths.RebootsLastBoot),
                FactViewColumn.Fact("Reboots (30d)", FactPaths.RebootsCount30d),
            ],
            FactViewKind.Properties,
            FactViewGroup.Software
        ),

        new(
            "Reboot History",
            [
                FactViewColumn.Key("#"),
                FactViewColumn.Fact("Boot time", FactPaths.RebootBootTime),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        new(
            "Security",
            [
                FactViewColumn.Fact("Firewall", FactPaths.SecurityFirewallEnabled),
                FactViewColumn.Fact("Firewall provider", FactPaths.SecurityFirewallProvider),
                FactViewColumn.Fact("Antivirus", FactPaths.SecurityAvName),
                FactViewColumn.Fact("AV enabled", FactPaths.SecurityAvEnabled),
                FactViewColumn.Fact("AV up to date", FactPaths.SecurityAvUpToDate),
                FactViewColumn.Fact("Secure Boot", FactPaths.SecuritySecureBoot),
                FactViewColumn.Fact("TPM present", FactPaths.SecurityTpmPresent),
                FactViewColumn.Fact("TPM version", FactPaths.SecurityTpmVersion),
                FactViewColumn.Fact("SELinux", FactPaths.SecuritySeLinuxMode),
                FactViewColumn.Fact("AppArmor", FactPaths.SecurityAppArmor),
                FactViewColumn.Fact("SIP (macOS)", FactPaths.SecuritySip),
                FactViewColumn.Fact("Gatekeeper (macOS)", FactPaths.SecurityGatekeeper),
                FactViewColumn.Fact("Defender enabled", FactPaths.SecurityDefenderEnabled),
                FactViewColumn.Fact("Defender real-time", FactPaths.SecurityDefenderRealtimeProtected),
                FactViewColumn.Fact("Defender sig age (days)", FactPaths.SecurityDefenderSignatureAgeDays),
                FactViewColumn.Fact("Defender sig version", FactPaths.SecurityDefenderSignatureVersion),
            ],
            FactViewKind.Properties,
            FactViewGroup.Security
        ),

        // Granular SMART counters moved off proj_disks (migration 0061): the headline fields
        // (health/temp/wear/power-on-hours) stay projected for the Storage report; these 9 are
        // display-only, read by nothing before the move.
        new(
            "Disk SMART Details",
            [
                FactViewColumn.Key("Disk"),
                FactViewColumn.Fact("Power cycles", FactPaths.DiskSmartPowerCycles),
                FactViewColumn.Fact("Reallocated sectors", FactPaths.DiskSmartReallocSectors),
                FactViewColumn.Fact("Uncorrectable errors", FactPaths.DiskSmartUncorrErrors),
                FactViewColumn.Fact("Pending sectors", FactPaths.DiskSmartPendingSectors),
                FactViewColumn.Fact("CRC errors", FactPaths.DiskSmartCrcErrors),
                FactViewColumn.Fact("Percentage used (NVMe)", FactPaths.DiskSmartPercentageUsed),
                FactViewColumn.Fact("Available spare % (NVMe)", FactPaths.DiskSmartAvailableSparePct),
                FactViewColumn.Fact("Data read (GB)", FactPaths.DiskSmartDataReadGB),
                FactViewColumn.Fact("Data written (GB)", FactPaths.DiskSmartDataWrittenGB),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        ),

        new(
            "Encrypted Volumes",
            [
                FactViewColumn.Fact("Device", FactPaths.SecurityEncryptedVolumeDevice),
                FactViewColumn.Fact("Mountpoint", FactPaths.SecurityEncryptedVolumeMountpoint),
                FactViewColumn.Fact("Type", FactPaths.SecurityEncryptedVolumeType),
                FactViewColumn.Fact("Status", FactPaths.SecurityEncryptedVolumeStatus),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        new(
            "SNMP",
            [
                FactViewColumn.Fact("System name", FactPaths.SnmpSysName),
                FactViewColumn.Fact("Description", FactPaths.SnmpSysDescr),
                FactViewColumn.Fact("Location", FactPaths.SnmpSysLocation),
                FactViewColumn.Fact("Contact", FactPaths.SnmpSysContact),
                FactViewColumn.Fact("Object ID", FactPaths.SnmpSysObjectID),
                FactViewColumn.Fact("Engine ID", FactPaths.SnmpEngineId),
            ],
            FactViewKind.Properties,
            FactViewGroup.Protocols
        ),

        // proj_bacnet_device / proj_modbus_device dropped (unification pass, migration 0062):
        // vendor_name feeds DeviceVendorDerivation instead (proj_devices.vendor); everything
        // else here was already single-device display only, no cross-device query need.
        new(
            "BACnet Details",
            [
                FactViewColumn.Fact("Device instance", FactPaths.BacnetDeviceInstance),
                FactViewColumn.Fact("Vendor", FactPaths.BacnetVendorName),
                FactViewColumn.Fact("Vendor ID", FactPaths.BacnetVendorId),
                FactViewColumn.Fact("Model", FactPaths.BacnetModelName),
                FactViewColumn.Fact("Object name", FactPaths.BacnetObjectName),
                FactViewColumn.Fact("Firmware revision", FactPaths.BacnetFirmwareRevision),
                FactViewColumn.Fact("App software version", FactPaths.BacnetApplicationSoftwareVersion),
                FactViewColumn.Fact("Description", FactPaths.BacnetDescription),
                FactViewColumn.Fact("Location", FactPaths.BacnetLocation),
                FactViewColumn.Fact("System status", FactPaths.BacnetSystemStatus),
                FactViewColumn.Fact("Serial number", FactPaths.BacnetSerialNumber),
            ],
            FactViewKind.Properties,
            FactViewGroup.Protocols
        ),

        new(
            "Modbus Details",
            [
                FactViewColumn.Fact("Vendor", FactPaths.ModbusVendorName),
                FactViewColumn.Fact("Product code", FactPaths.ModbusProductCode),
                FactViewColumn.Fact("Revision", FactPaths.ModbusRevision),
            ],
            FactViewKind.Properties,
            FactViewGroup.Protocols
        ),

        new(
            "Modbus Holding Registers",
            [
                FactViewColumn.Key("Register"),
                FactViewColumn.Fact("Value", FactPaths.ModbusHoldingRegister)
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Protocols
        ),

        new(
            "Modbus Input Registers",
            [
                FactViewColumn.Key("Register"),
                FactViewColumn.Fact("Value", FactPaths.ModbusInputRegister)
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Protocols
        ),

        // FactPaths.DeviceVendor (Google Wifi's raw "Device[].Vendor" assertion) is now an input
        // to DeviceVendorDerivation rather than separately projected — kept inspectable here.
        // Discovery-sourced, not software state, alongside its sibling "Discovered: Identity".
        new(
            "Device Identity (Raw)",
            [FactViewColumn.Fact("Vendor (raw)", FactPaths.DeviceVendor)],
            FactViewKind.Properties,
            FactViewGroup.Discovery
        ),

        new(
            "Docker",
            [
                FactViewColumn.Fact("Version", FactPaths.DockerVersion),
                FactViewColumn.Fact("API version", FactPaths.DockerApiVersion),
                FactViewColumn.Fact("Storage driver", FactPaths.DockerStorageDriver),
                FactViewColumn.Fact("Containers running", FactPaths.DockerContainersRunning),
                FactViewColumn.Fact("Containers paused", FactPaths.DockerContainersPaused),
                FactViewColumn.Fact("Containers stopped", FactPaths.DockerContainersStopped),
                FactViewColumn.Fact("Images", FactPaths.DockerImages),
                FactViewColumn.Fact("OS", FactPaths.DockerOS),
                FactViewColumn.Fact("Kernel", FactPaths.DockerKernel),
                FactViewColumn.Fact("Memory (bytes)", FactPaths.DockerMemBytes),
                FactViewColumn.Fact("CPUs", FactPaths.DockerCpuCount),
            ],
            FactViewKind.Properties,
            FactViewGroup.Software
        ),

        // FV-14 hardware extras (board / BIOS / arch / install date — no projection)
        new(
            "Hardware Details",
            [
                FactViewColumn.Fact("Board vendor", FactPaths.HwBoardVendor),
                FactViewColumn.Fact("Board model", FactPaths.HwBoardModel),
                FactViewColumn.Fact("BIOS vendor", FactPaths.HwBiosVendor),
                FactViewColumn.Fact("BIOS date", FactPaths.HwBiosDate),
                FactViewColumn.Fact("Chassis vendor", FactPaths.HwChassisVendor),
                FactViewColumn.Fact("Chassis type", FactPaths.HwChassisType),
                FactViewColumn.Fact("Chassis serial", FactPaths.HwChassisSerial),
                FactViewColumn.Fact("Chassis asset tag", FactPaths.HwChassisAssetTag),
                FactViewColumn.Fact("OS arch", FactPaths.HwOSArch),
                FactViewColumn.Fact("Install date", FactPaths.SystemInstallDate),
            ],
            FactViewKind.Properties,
            FactViewGroup.Hardware
        ),

        // FV-13 resolver config (no projection)
        new(
            "DNS Servers",
            [FactViewColumn.Key("#"), FactViewColumn.Fact("Server", FactPaths.NetworkDnsServer)],
            FactViewKind.List,
            Group: FactViewGroup.Network
        ),
        new(
            "DNS Search Domains",
            [FactViewColumn.Key("#"), FactViewColumn.Fact("Domain", FactPaths.NetworkDnsSearch)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),
        // FV-15 agentless SSH-probed host facts (no projection). Same kind of data as
        // "Resource Usage" (CPU/memory stats), just from an SSH probe instead of the full
        // agent — grouped with it under Hardware rather than Software for consistency.
        new(
            "Agentless Host (SSH)",
            [
                FactViewColumn.Fact("CPU count", FactPaths.SshCpuCount),
                FactViewColumn.Fact("Memory total (MB)", FactPaths.SshMemTotalMB),
                FactViewColumn.Fact("Memory used (MB)", FactPaths.SshMemUsedMB),
            ],
            FactViewKind.Properties,
            FactViewGroup.Hardware
        ),
        new(
            "SSH-Probed Filesystems",
            [
                FactViewColumn.Key("Filesystem"),
                FactViewColumn.Fact("Size", FactPaths.SshFsSize),
                FactViewColumn.Fact("Used", FactPaths.SshFsUsed),
                FactViewColumn.Fact("Use %", FactPaths.SshFsUsePercent),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        ),

        // FV-16 detail-only interface metadata that is not on proj_interfaces (dropped mig 0042
        // or never projected). Keyed by interface, one row per NIC. Absorbs the former standalone
        // "Interface DNS" and "SSH-Probed Interfaces" views (same Device|Interface dimension key,
        // one column each) — no reason to make an operator click through three separate
        // single-purpose tables for facts about the same interface.
        new(
            "Interface Details",
            [
                FactViewColumn.Key("Interface"),
                FactViewColumn.Fact("Alias", FactPaths.InterfaceAlias),
                FactViewColumn.Fact("Permanent MAC", FactPaths.InterfacePermMAC),
                FactViewColumn.Fact("Admin status", FactPaths.InterfaceAdminStatus),
                FactViewColumn.Fact("Oper status", FactPaths.InterfaceOperStatus),
                FactViewColumn.Fact("Gateway", FactPaths.InterfaceGateway),
                FactViewColumn.Fact("DHCP Server", FactPaths.InterfaceDhcpServer),
                FactViewColumn.Fact("Connection type", FactPaths.InterfaceConnectionType),
                FactViewColumn.Fact("ISP type", FactPaths.InterfaceIspType),
                FactViewColumn.Fact("VLAN", FactPaths.InterfaceVlanId),
                FactViewColumn.Fact("Tagged VLANs", FactPaths.InterfaceTaggedVlans),
                FactViewColumn.Fact("Bridge master", FactPaths.InterfaceBridgeMaster),
                FactViewColumn.Fact("STP state", FactPaths.InterfaceStpState),
                FactViewColumn.Fact("STP role", FactPaths.InterfaceStpRole),
                FactViewColumn.Fact("STP cost", FactPaths.InterfaceStpCost),
                FactViewColumn.Fact("DNS", FactPaths.InterfaceDns),
                FactViewColumn.Fact("IP (SSH-probed)", FactPaths.SshInterfaceIP),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        // Bridge-level STP root election (docs/plans/d3-l2-l3.md) — one row per bridge
        // (e.g. "br-lan", or a switch's single implicit bridge). Sourced from OnHub
        // (brctl showstp) or SNMP BRIDGE-MIB dot1dStp* scalars.
        new(
            "Spanning Tree",
            [
                FactViewColumn.Key("Bridge"),
                FactViewColumn.Fact("STP enabled", FactPaths.BridgeStpEnabled),
                FactViewColumn.Fact("Bridge ID", FactPaths.BridgeId),
                FactViewColumn.Fact("Root ID", FactPaths.BridgeRootId),
                FactViewColumn.Fact("Root path cost", FactPaths.BridgeRootPathCost),
                FactViewColumn.Fact("Root port", FactPaths.BridgeRootPort),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        // L2 neighbor adjacency (docs/plans/d3-l2-l3.md) — one row per LLDP-discovered
        // neighbor on this device. Display-only today; the L2 topology graph API reads
        // these facts directly rather than through this view.
        new(
            "Neighbors",
            [
                FactViewColumn.Key("Neighbor"),
                FactViewColumn.Fact("Local port", FactPaths.NeighborLocalPort),
                FactViewColumn.Fact("Remote chassis ID", FactPaths.NeighborRemoteChassisId),
                FactViewColumn.Fact("Remote port ID", FactPaths.NeighborRemotePortId),
                FactViewColumn.Fact("Remote system name", FactPaths.NeighborRemoteSysName),
                FactViewColumn.Fact("Remote MAC", FactPaths.NeighborRemoteMac),
                FactViewColumn.Fact("Remote IP", FactPaths.NeighborRemoteIp),
                FactViewColumn.Fact("Protocol", FactPaths.NeighborProtocol),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        // Self-reported ARP/neighbor cache (Device[].ARP[], key = IP address). Distinct from
        // "Neighbors" above (LLDP adjacency, key = local port) — this is the device's own OS
        // ARP table, previously never surfaced anywhere but the raw All Facts dump.
        new(
            "ARP Cache",
            [
                FactViewColumn.Key("IP"),
                FactViewColumn.Fact("MAC", FactPaths.ArpMac),
                FactViewColumn.Fact("Interface", FactPaths.ArpInterface),
                FactViewColumn.Fact("State", FactPaths.ArpState),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        // Google Wifi/OnHub's own periodic on-device WAN speed test (diagnostic-report
        // infra_state.wan_speed_test_results) — one row per device, not per-interface.
        new(
            "Google Wifi WAN Speed Test",
            [
                FactViewColumn.Fact("Tested at", FactPaths.NetworkWanSpeedTestAt),
                FactViewColumn.Fact("Download bytes/sec", FactPaths.NetworkWanSpeedTestDownloadBps),
                FactViewColumn.Fact("Upload bytes/sec", FactPaths.NetworkWanSpeedTestUploadBps),
                FactViewColumn.Fact("Total downloaded", FactPaths.NetworkWanSpeedTestTotalDownloadedBytes),
                FactViewColumn.Fact("Total uploaded", FactPaths.NetworkWanSpeedTestTotalUploadedBytes),
            ],
            FactViewKind.Properties,
            FactViewGroup.Network
        ),
        new(
            "Interface Counters",
            [
                FactViewColumn.Key("Interface"),
                FactViewColumn.Fact("Rx bytes", FactPaths.InterfaceRxBytes),
                FactViewColumn.Fact("Tx bytes", FactPaths.InterfaceTxBytes),
                FactViewColumn.Fact("Rx packets", FactPaths.InterfaceRxPackets),
                FactViewColumn.Fact("Tx packets", FactPaths.InterfaceTxPackets),
                FactViewColumn.Fact("Total bytes", FactPaths.Derived.InterfaceTotalBytes),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        ),

        // FV-17 detail-only disk metadata not on proj_disks.
        new(
            "Disk Details",
            [
                FactViewColumn.Key("Disk"),
                FactViewColumn.Fact("Serial", FactPaths.DiskSerial),
                FactViewColumn.Fact("Removable", FactPaths.DiskRemovable),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        ),

        // FV-18 detail-only container metadata not on proj_containers.
        new(
            "Container Details",
            [
                FactViewColumn.Key("Container"),
                FactViewColumn.Fact("Status", FactPaths.ContainerStatus),
                FactViewColumn.Fact("Created (epoch)", FactPaths.ContainerCreated),
                FactViewColumn.Fact("Ports", FactPaths.ContainerPorts),
                FactViewColumn.Fact("Mounts", FactPaths.ContainerMounts),
                FactViewColumn.Fact("Labels", FactPaths.ContainerLabels),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),

        // FV-19 Windows per-profile firewall state (a bare list; each element is one profile
        // and its on/off state). Previously escaped routing via the list-sink loophole.
        new(
            "Firewall Profiles",
            [FactViewColumn.Key("#"), FactViewColumn.Fact("Profile", FactPaths.SecurityFirewallProfile)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Security
        ),

        // FV-20 banner / protocol facts observed against a discovered IP (not on proj_discovered —
        // device-detail only). Every scanner signal is a typed fact routed here (or to the TLS /
        // sub-dimension views below); there is no raw Attr[] sink. Split by protocol (one view per
        // probe type, all sharing the "Discovered" dimension key) rather than one grab-bag table:
        // a device only ever answers a handful of these probes, so RenderList's "return null when
        // no facts match" already hides every irrelevant protocol's table for that device — one
        // wide sparse row per device became a tight, mostly-populated table per protocol instead.
        new(
            "Discovered: HTTP",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Title", FactPaths.DiscoveredHttpTitle),
                FactViewColumn.Fact("Server", FactPaths.DiscoveredHttpServer),
                FactViewColumn.Fact("Status", FactPaths.DiscoveredHttpStatus),
                FactViewColumn.Fact("URL", FactPaths.DiscoveredHttpUrl),
                FactViewColumn.Fact("Favicon MD5", FactPaths.DiscoveredFaviconMd5),
                FactViewColumn.Fact("Favicon mmh3", FactPaths.DiscoveredFaviconMmh3),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // Cross-protocol identity resolution derived from the probe signals above (OS/firmware/
        // serial guesses + the confidence-scored source that won) — kept separate from the raw
        // HTTP banner fields since it's a resolved summary, not a single protocol's response.
        new(
            "Discovered: Identity",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("OS", FactPaths.DiscoveredOs),
                // Written by NetworkDiscoveryCollector; never read by the materializer or any
                // other display query, so it stays projection-free (no proj_discovered column).
                FactViewColumn.Fact("Firmware", FactPaths.DiscoveredFirmware),
                FactViewColumn.Fact("Serial (HTTP)", FactPaths.DiscoveredHttpSerial),
                FactViewColumn.Fact("Serial (SNMP)", FactPaths.DiscoveredSnmpSerial),
                FactViewColumn.Fact("Confidence", FactPaths.DiscoveredHttpConfidence),
                FactViewColumn.Fact("Source", FactPaths.DiscoveredHttpIdentitySource),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // Printer-specific status/consumables/hardware detail (HP LEDM / Epson EWS follow-ups) —
        // kept separate from "Discovered: Identity" since it's telemetry, not identity.
        new(
            "Discovered: Printer",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Status", FactPaths.DiscoveredPrinterStatus),
                FactViewColumn.Fact("Alerts", FactPaths.DiscoveredPrinterAlerts),
                FactViewColumn.Fact("Consumables", FactPaths.DiscoveredPrinterConsumables),
                FactViewColumn.Fact("Product number", FactPaths.DiscoveredPrinterProductNumber),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: SMB",
            [FactViewColumn.Key("Discovered"), FactViewColumn.Fact("Dialect", FactPaths.DiscoveredSmb2Dialect)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: SSH",
            [FactViewColumn.Key("Discovered"), FactViewColumn.Fact("Banner", FactPaths.DiscoveredSshBanner)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: SSDP/UPnP",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("SSDP Server", FactPaths.DiscoveredSsdpServer),
                FactViewColumn.Fact("SSDP ST", FactPaths.DiscoveredSsdpSt),
                FactViewColumn.Fact("Device Type", FactPaths.DiscoveredUpnpDeviceType),
                FactViewColumn.Fact("Presentation URL", FactPaths.DiscoveredPresentationUrl),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: RTSP",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Server", FactPaths.DiscoveredRtspServer),
                FactViewColumn.Fact("Content-Type", FactPaths.DiscoveredRtspContentType),
                FactViewColumn.Fact("Methods", FactPaths.DiscoveredRtspMethods),
                FactViewColumn.Fact("Port", FactPaths.DiscoveredRtspPort),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: LDAP",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Naming Context", FactPaths.DiscoveredLdapNamingContext),
                FactViewColumn.Fact("Server Name", FactPaths.DiscoveredLdapServerName),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: MQTT",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Auth Required", FactPaths.DiscoveredMqttAuthRequired),
                FactViewColumn.Fact("Return Code", FactPaths.DiscoveredMqttReturnCode),
                FactViewColumn.Fact("Port", FactPaths.DiscoveredMqttPort),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // Lightweight discovery-probe response for a Modbus-speaking device that isn't under full
        // collection — distinct from "Modbus Details", which comes from the dedicated ModbusCollector.
        new(
            "Discovered: Modbus",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Unit ID", FactPaths.DiscoveredModbusUnitId),
                FactViewColumn.Fact("Port", FactPaths.DiscoveredModbusPort),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // Lightweight discovery-probe response for a BACnet-speaking device that isn't under full
        // collection — distinct from "BACnet Details", which comes from the dedicated BacnetCollector.
        new(
            "Discovered: BACnet",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Instance", FactPaths.DiscoveredBacnetInstance),
                FactViewColumn.Fact("Vendor ID", FactPaths.DiscoveredBacnetVendorId),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: Hue",
            [FactViewColumn.Key("Discovered"), FactViewColumn.Fact("API Version", FactPaths.DiscoveredHueApiVersion)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: Cast",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Version", FactPaths.DiscoveredEurekaCastVersion),
                FactViewColumn.Fact("SSID", FactPaths.DiscoveredEurekaSsid),
                // Raw _googlecast mDNS TXT values (opaque, undocumented format — see
                // FactPaths.DiscoveredCastCapabilities). Kept alongside the other raw
                // discovered-probe signals rather than a projection: no cross-device query need.
                FactViewColumn.Fact("Capabilities", FactPaths.DiscoveredCastCapabilities),
                FactViewColumn.Fact("Status", FactPaths.DiscoveredLinkCastStatus),
                FactViewColumn.Fact("Running App", FactPaths.DiscoveredLinkCastRunningApp),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: IPP",
            [FactViewColumn.Key("Discovered"), FactViewColumn.Fact("Location", FactPaths.DiscoveredIppLocation)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: AirPlay",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Features", FactPaths.DiscoveredAirplayFeatures),
                FactViewColumn.Fact("Plist Format", FactPaths.DiscoveredAirplayPlistFormat),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: WSD",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Types", FactPaths.DiscoveredWsdTypes),
                FactViewColumn.Fact("Metadata Version", FactPaths.DiscoveredWsdMetadataVersion),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: ONVIF",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Auth Required", FactPaths.DiscoveredOnvifAuthRequired),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: NBNS",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("OpCode", FactPaths.DiscoveredNbnsOpCode),
                FactViewColumn.Fact("Result Code", FactPaths.DiscoveredNbnsResultCode),
                FactViewColumn.Fact("Authoritative", FactPaths.DiscoveredNbnsAuthoritative),
                FactViewColumn.Fact("Truncated", FactPaths.DiscoveredNbnsTruncated),
                FactViewColumn.Fact("Broadcast", FactPaths.DiscoveredNbnsBroadcast),
                FactViewColumn.Fact("Recursion Desired", FactPaths.DiscoveredNbnsRecursionDesired),
                FactViewColumn.Fact("Recursion Available", FactPaths.DiscoveredNbnsRecursionAvailable),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // A stable device property (the owner explicitly reserved this IP in the router UI),
        // not a per-sighting metric — kept separate from Sighting Telemetry below, which is
        // Link.* (per-observation) data.
        new(
            "Discovered: Google Wifi DHCP Reservation",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("DHCP Reserved", FactPaths.DiscoveredIsDhcpReserved),
                // Display-only: the OnHub cannot report true DHCP lease data (no expiry/renewal
                // anywhere in the diagnostic report), only the static reservation's configured IP.
                FactViewColumn.Fact("Reserved IP", FactPaths.DiscoveredDhcpReservedIp),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // Per-sighting WiFi link telemetry (Google Wifi/OnHub). Moved off proj_discovered
        // (migration 0059): no join/cross-device-query need, and among the highest
        // write-frequency columns in that table (updated on every WiFi scan cycle) for data
        // that only ever needs to render on one device's page. The Sightings tab (a real
        // projection-backed query — it needs the computed OUI/observer-hostname joins a fact
        // view can't do) still shows Observer/IP/Sources/OUI/Services; this table carries the
        // per-observation link metrics that used to live in those same rows.
        new(
            "Sighting Telemetry",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Medium", FactPaths.DiscoveredLinkMedium),
                FactViewColumn.Fact("Band", FactPaths.DiscoveredLinkBand),
                FactViewColumn.Fact("Guest", FactPaths.DiscoveredLinkGuest),
                FactViewColumn.Fact("Signal (dBm)", FactPaths.DiscoveredLinkSignalDbm),
                FactViewColumn.Fact("Tx Rate (Mbps)", FactPaths.DiscoveredLinkTxRateMbps),
                FactViewColumn.Fact("Rx Rate (Mbps)", FactPaths.DiscoveredLinkRxRateMbps),
                FactViewColumn.Fact("Rx Bytes", FactPaths.DiscoveredLinkRxBytes),
                FactViewColumn.Fact("Tx Bytes", FactPaths.DiscoveredLinkTxBytes),
                FactViewColumn.Fact("Connected (s)", FactPaths.DiscoveredLinkConnectedSeconds),
                FactViewColumn.Fact("Mesh AP BSSID", FactPaths.DiscoveredLinkMeshApBssid),
                FactViewColumn.Fact("Last Active At", FactPaths.DiscoveredLinkLastActiveAt),
                FactViewColumn.Fact("Last Active Interface", FactPaths.DiscoveredLinkLastActiveInterface),
                FactViewColumn.Fact("Last Roaming At", FactPaths.DiscoveredLinkLastRoamingAt),
                FactViewColumn.Fact("Last Roaming AP IP", FactPaths.DiscoveredLinkLastRoamingApIp),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // The certificate a discovered host presents on a TLS probe (from TlsCertScanner).
        new(
            "Discovered TLS Certificate",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("CN", FactPaths.DiscoveredTlsCn),
                FactViewColumn.Fact("Subject", FactPaths.DiscoveredTlsSubject),
                FactViewColumn.Fact("Issuer", FactPaths.DiscoveredTlsIssuer),
                FactViewColumn.Fact("Serial", FactPaths.DiscoveredTlsSerial),
                FactViewColumn.Fact("Not After", FactPaths.DiscoveredTlsNotAfter),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        // NBNS name-table entries: nested one level deeper than "Discovered: NBNS" above
        // (Device|Discovered|NbnsName vs Device|Discovered), so it's a separate view — a List
        // view's columns must share one DimKey. Renders fine on the existing generic
        // FactViewRenderer (DimKey/Key grouping isn't limited to two segments); the row key is
        // the discovered peer and NBNS name joined ("192.168.1.5 · WORKGROUP-PC"). No projection
        // needed: proj_discovered_nbns_names has no reader anywhere in the codebase today (only
        // written by ProjectionLibrary and swept by device-merge/retention), so per FactViewDef's
        // own rule ("reserve projections for cross-device queries") this belongs here, not there.
        new(
            "Discovered: NBNS Names",
            [
                FactViewColumn.Key("Discovered / Name"),
                FactViewColumn.Fact("Name", FactPaths.DiscoveredNbnsName),
                FactViewColumn.Fact("Suffix", FactPaths.DiscoveredNbnsSuffix),
                FactViewColumn.Fact("Suffix Description", FactPaths.DiscoveredNbnsSuffixDescription),
                FactViewColumn.Fact("Owner Node Type", FactPaths.DiscoveredNbnsOwnerNodeType),
                FactViewColumn.Fact("Group", FactPaths.DiscoveredNbnsIsGroup),
                FactViewColumn.Fact("Permanent", FactPaths.DiscoveredNbnsIsPermanent),
                FactViewColumn.Fact("Active", FactPaths.DiscoveredNbnsIsActive),
                FactViewColumn.Fact("In Conflict", FactPaths.DiscoveredNbnsIsInConflict),
                FactViewColumn.Fact("Being Deregistered", FactPaths.DiscoveredNbnsIsBeingDeregistered),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // CoAP resources/content-formats per discovered neighbor — same shape and same fix as
        // "Discovered: NBNS Names" above: nested one level under Discovered, no reader anywhere
        // for the equivalent proj_discovered_coap_resources/_formats tables, so replaced by
        // fact views straight off facts_history instead of a projection.
        new(
            "Discovered: CoAP Resources",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Resource Path", FactPaths.DiscoveredCoapResource),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        new(
            "Discovered: CoAP Content Formats",
            [
                FactViewColumn.Key("Discovered"),
                FactViewColumn.Fact("Content Format ID", FactPaths.DiscoveredCoapContentFormat),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),

        // FV-21 hardware inventory components (dmidecode / lshw / SNMP entPhysical). The raw
        // per-component Details JSON has no typed projection column — surfaced here alongside
        // the identifying fields, keyed by the stable component handle.
        new(
            "Hardware Components",
            [
                FactViewColumn.Key("Component"),
                FactViewColumn.Fact("Class", FactPaths.HwComponentClass),
                FactViewColumn.Fact("Vendor", FactPaths.HwComponentVendor),
                FactViewColumn.Fact("Model", FactPaths.HwComponentModel),
                FactViewColumn.Fact("Serial", FactPaths.HwComponentSerial),
                FactViewColumn.Fact("Details", FactPaths.HwComponentDetails),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Hardware
        ),
    ];

    /// <summary>Service-detail fact views (rendered on the service page from Service-keyed facts).</summary>
    public static readonly IReadOnlyList<FactViewDef> Service =
    [
        new(
            "Home Assistant",
            [
                FactViewColumn.Fact("Core version", ServicePaths.HomeAssistantCoreVersion),
                FactViewColumn.Fact("Supervisor version", ServicePaths.HomeAssistantSupervisorVersion),
                FactViewColumn.Fact("OS version", ServicePaths.HomeAssistantOsVersion),
                FactViewColumn.Fact("OS board", ServicePaths.HomeAssistantOsBoard),
                FactViewColumn.Fact("Channel", ServicePaths.HomeAssistantChannel),
                FactViewColumn.Fact("Hostname", ServicePaths.HomeAssistantHostname),
                FactViewColumn.Fact("Device ID", ServicePaths.HomeAssistantDeviceId),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Hardware
        ),
        new(
            "DHCP Leases",
            [
                FactViewColumn.Fact("IP", ServicePaths.DhcpLeaseIP),
                FactViewColumn.Fact("Hostname", ServicePaths.DhcpLeaseHostname),
                FactViewColumn.Fact("Type", ServicePaths.DhcpLeaseType),
                FactViewColumn.Fact("Expires", ServicePaths.DhcpLeaseExpires),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Protocols
        ),
        new(
            "Add-ons",
            [
                FactViewColumn.Fact("Name", ServicePaths.HomeAssistantAddOnName),
                FactViewColumn.Fact("Version", ServicePaths.HomeAssistantAddOnVersion),
                FactViewColumn.Fact("State", ServicePaths.HomeAssistantAddOnState),
                FactViewColumn.Fact("Update available", ServicePaths.HomeAssistantAddOnUpdateAvailable),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Software
        ),
        new(
            "Home Assistant Devices",
            [
                FactViewColumn.Fact("Name", ServicePaths.HomeAssistantHaDeviceName),
                FactViewColumn.Fact("Manufacturer", ServicePaths.HomeAssistantHaDeviceManufacturer),
                FactViewColumn.Fact("Model", ServicePaths.HomeAssistantHaDeviceModel),
                FactViewColumn.Fact("Model ID", ServicePaths.HomeAssistantHaDeviceModelId),
                FactViewColumn.Fact("Hw version", ServicePaths.HomeAssistantHaDeviceHwVersion),
                FactViewColumn.Fact("Sw version", ServicePaths.HomeAssistantHaDeviceSwVersion),
                FactViewColumn.Fact("Serial number", ServicePaths.HomeAssistantHaDeviceSerialNumber),
                FactViewColumn.Fact("Labels", ServicePaths.HomeAssistantHaDeviceLabels),
                FactViewColumn.Fact("Area", ServicePaths.HomeAssistantHaDeviceAreaName),
                FactViewColumn.Fact("Identifiers", ServicePaths.HomeAssistantHaDeviceIdentifiers),
                FactViewColumn.Fact("MAC", ServicePaths.HomeAssistantHaDeviceMac),
                FactViewColumn.Fact("UPnP UUID", ServicePaths.HomeAssistantHaDeviceUpnpUuid),
                FactViewColumn.Fact("Via device", ServicePaths.HomeAssistantHaDeviceViaDeviceKey),
                FactViewColumn.Fact("Online", ServicePaths.HomeAssistantHaDeviceOnline),
                FactViewColumn.Fact("Battery %", ServicePaths.HomeAssistantHaDeviceBatteryPercent),
                FactViewColumn.Fact("Update available", ServicePaths.HomeAssistantHaDeviceUpdateAvailable),
                FactViewColumn.Fact("Latest version", ServicePaths.HomeAssistantHaDeviceLatestVersion),
                // docs/plans/ha-device-enrichment.md §4 — device-class-scoped enrichment,
                // sharing this view's DimKey (Service|HaDevice).
                FactViewColumn.Fact("Wi-Fi IP", ServicePaths.HomeAssistantHaDeviceWifiIp),
                FactViewColumn.Fact("Wi-Fi BSSID", ServicePaths.HomeAssistantHaDeviceWifiBssid),
                FactViewColumn.Fact("Wi-Fi link speed (Mbps)", ServicePaths.HomeAssistantHaDeviceWifiLinkSpeedMbps),
                FactViewColumn.Fact("Wi-Fi signal (dBm)", ServicePaths.HomeAssistantHaDeviceWifiSignalStrengthDbm),
                FactViewColumn.Fact("Uptime (s)", ServicePaths.HomeAssistantHaDeviceUptimeSeconds),
                FactViewColumn.Fact("WAN online", ServicePaths.HomeAssistantHaDeviceWanOnline),
                FactViewColumn.Fact("WAN download (bps)", ServicePaths.HomeAssistantHaDeviceWanDownloadBps),
                FactViewColumn.Fact("WAN upload (bps)", ServicePaths.HomeAssistantHaDeviceWanUploadBps),
                FactViewColumn.Fact("Camera URL", ServicePaths.HomeAssistantHaDeviceCameraUrl),
                FactViewColumn.Fact("Last ring", ServicePaths.HomeAssistantHaDeviceLastRingAt),
                FactViewColumn.Fact("Ring count", ServicePaths.HomeAssistantHaDeviceRingCount),
                FactViewColumn.Fact("Last motion", ServicePaths.HomeAssistantHaDeviceLastMotionAt),
                FactViewColumn.Fact("Motion count", ServicePaths.HomeAssistantHaDeviceMotionCount),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        // Ink/toner cartridges, one row per cartridge — nested one level deeper than "Home
        // Assistant Devices" above (Service|HaDevice|InkCartridge vs Service|HaDevice), so a
        // separate view, same fix as "Discovered: NBNS Names" (a List view's columns must
        // share one DimKey). No projection: single-device detail only, no cross-device query.
        new(
            "Home Assistant Device Ink Cartridges",
            [
                FactViewColumn.Key("Device / Cartridge"),
                FactViewColumn.Fact("Level %", ServicePaths.HomeAssistantHaDeviceInkCartridgeLevel),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        new(
            "CA DNS Names",
            [
                FactViewColumn.Fact("DNS Name", ServicePaths.CaDnsName),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        new(
            "Top Queried Domains",
            [FactViewColumn.Key("Domain"), FactViewColumn.Fact("Hits", ServicePaths.DnsTopQueried)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        new(
            "Top Blocked Domains",
            [FactViewColumn.Key("Domain"), FactViewColumn.Fact("Hits", ServicePaths.DnsTopBlocked)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
        new(
            "Top Clients",
            [FactViewColumn.Key("Client"), FactViewColumn.Fact("Queries", ServicePaths.DnsTopClients)],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Discovery
        ),
    ];

    /// <summary>
    /// Every FactPaths/ServicePaths value consumed by any view (device or service) —
    /// used by the routing fitness test to recognise a fact whose home is a view.
    /// </summary>
    public static IReadOnlyCollection<string> AllConsumedFactPaths() =>
        All.Concat(Service)
            .SelectMany(v => v.Columns)
            .Select(c => c.FactPath)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToHashSet(StringComparer.Ordinal);
}