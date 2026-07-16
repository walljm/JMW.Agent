using JMW.Discovery.Core.Analysis;

using Npgsql;

using NpgsqlTypes;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// Creates all GenericProjection instances for the JMW Agent network discovery system.
/// Each projection maintains a queryable current-state table derived from incoming facts.
/// </summary>
public static class ProjectionLibrary
{
    public static IReadOnlyList<IProjection> CreateAll(
        NpgsqlDataSource db,
        int maxCacheEntries = 500_000
    ) =>
    [
        new GenericProjection(
            new ProjectionDef(
                "proj_devices",
                ["Device"],
                [
                    // Fed by DeviceVendorDerivation, not FactPaths.DeviceVendor directly — that raw
                    // fact is one of several inputs fanned into the canonical output below (see
                    // DeviceVendorDerivation.cs). Every cross-device report reads THIS column.
                    new(FactPaths.Derived.DeviceVendorCanonical, "vendor", NpgsqlDbType.Text),
                    // Inferred guess (VendorFromOsDistroDerivation et al.) — reporting should only
                    // consult this when `vendor` is NULL. See docs/plans/vendor-derivation-updates.md §3.
                    new(FactPaths.Derived.DeviceVendorGuess, "vendor_guess", NpgsqlDbType.Text),
                    new(FactPaths.DeviceKind, "kind", NpgsqlDbType.Text),
                    // Fanned in from whichever raw model field is present (DeviceModelDerivation),
                    // vendor+OS-dispatched cleanup applied on top — see FactPaths.Derived.DeviceModelCanonical.
                    new(FactPaths.Derived.DeviceModelCanonical, "model", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        new GenericProjection(
            new ProjectionDef(
                "proj_systems",
                ["Device"],
                [
                    new(FactPaths.SystemHostname, "hostname", NpgsqlDbType.Text),
                    new(FactPaths.SystemOsFamily, "os_family", NpgsqlDbType.Text),
                    new(FactPaths.SystemOsDistro, "os_distro", NpgsqlDbType.Text),
                    // Inferred guess (VendorOsFromDeviceBannerDerivation) — reporting should only
                    // consult this when `os_distro` is NULL. See vendor-derivation-updates.md §5.
                    new(FactPaths.Derived.DeviceOsGuess, "os_distro_guess", NpgsqlDbType.Text),
                    // os_version/os_build/kernel/kernel_arch/timezone/boot_time/uptime_seconds and
                    // the live cpu/mem/load metrics moved to the "OS Details" / "Resource Usage"
                    // fact views (FactViewLibrary.cs) — single-device display only, no cross-device
                    // query need, and the resource metrics were unread by anything at all.
                ]
            )
            {
                Indexes =
                [
                    new("proj_systems_hostname_idx", ["hostname", "device"]),
                    new("ix_proj_systems_hostname_trgm", ["hostname"], ProjectionIndexMethod.GinTrgm),
                ],
            },
            maxCacheEntries
        ),

        new GenericProjection(
            new ProjectionDef(
                "proj_hardware",
                ["Device"],
                [
                    new(FactPaths.HwCpuModel, "cpu_model", NpgsqlDbType.Text),
                    new(FactPaths.HwCpuVendor, "cpu_vendor", NpgsqlDbType.Text),
                    new(FactPaths.HwCpuCores, "cpu_cores", NpgsqlDbType.Bigint),
                    new(FactPaths.HwCpuLogicalCores, "cpu_logical_cores", NpgsqlDbType.Bigint),
                    new(FactPaths.HwCpuMhz, "cpu_mhz", NpgsqlDbType.Double),
                    new(FactPaths.HwTotalMemBytes, "total_mem_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.HwSystemVendor, "system_vendor", NpgsqlDbType.Text),
                    new(FactPaths.HwSystemModel, "system_model", NpgsqlDbType.Text),
                    new(FactPaths.HwSystemSerial, "system_serial", NpgsqlDbType.Text),
                    new(FactPaths.HwBiosVersion, "bios_version", NpgsqlDbType.Text),
                    new(FactPaths.HwVirtualization, "virtualization", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // Key = MAC address (normalized 12-hex form), or "snmp-if-{idx}" for no-MAC interfaces.
        // SpeedBps: collector converts Mbps × 1_000_000. TotalBytes is derived (RxBytes + TxBytes).
        new GenericProjection(
            new ProjectionDef(
                "proj_interfaces",
                ["Device", "Interface"],
                [
                    new(FactPaths.InterfaceName, "name", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceMAC, "mac_address", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceObscuredMAC, "obscured_mac", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceMTU, "mtu", NpgsqlDbType.Bigint),
                    new(FactPaths.InterfaceUp, "up", NpgsqlDbType.Boolean),
                    new(FactPaths.InterfaceLoopback, "loopback", NpgsqlDbType.Boolean),
                    new(FactPaths.InterfaceSpeedBps, "speed_bps", NpgsqlDbType.Bigint),
                    new(FactPaths.InterfaceDuplex, "duplex", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceType, "type", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceIPv4, "ipv4", NpgsqlDbType.Text),
                    new(FactPaths.InterfaceIPv6, "ipv6", NpgsqlDbType.Text),
                    // Populated only by collectors (Google Wifi/OnHub) that must emit a bare IP
                    // for ipv4/ipv6 above (that bare-IP meaning is an exact-match join key
                    // elsewhere — DiscoveryMaterializer's MAC reconstruction). SubnetsApi uses
                    // these to synthesize a CIDR when ipv4/ipv6 itself carries no "/".
                    new(FactPaths.InterfaceIPv4PrefixLength, "ipv4_prefix_length", NpgsqlDbType.Integer),
                    new(FactPaths.InterfaceIPv6PrefixLength, "ipv6_prefix_length", NpgsqlDbType.Integer),
                ]
            ),
            maxCacheEntries
        ),

        // Key = serial number (or disk name when no serial available).
        new GenericProjection(
            new ProjectionDef(
                "proj_disks",
                ["Device", "Disk"],
                [
                    new(FactPaths.DiskName, "name", NpgsqlDbType.Text),
                    new(FactPaths.DiskModel, "model", NpgsqlDbType.Text),
                    new(FactPaths.DiskSizeBytes, "size_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.DiskType, "type", NpgsqlDbType.Text),
                    new(FactPaths.DiskSmartOverallHealth, "smart_health", NpgsqlDbType.Text),
                    new(FactPaths.DiskSmartTempC, "smart_temp_c", NpgsqlDbType.Double),
                    new(FactPaths.DiskSmartPowerOnHours, "smart_power_on_hours", NpgsqlDbType.Bigint),
                    new(FactPaths.DiskSmartWearPercent, "smart_wear_pct", NpgsqlDbType.Double),
                    // The 9 granular SMART counters (power cycles, reallocated/pending sectors, CRC
                    // errors, etc.) moved to the "Disk SMART Details" fact view (migration 0061) —
                    // read by nothing (not even single-device display) before the move.
                ]
            ),
            maxCacheEntries
        ),

        // Key = mountpoint path. UsedPercent is derived.
        new GenericProjection(
            new ProjectionDef(
                "proj_filesystems",
                ["Device", "Filesystem"],
                [
                    new(FactPaths.FsType, "fs_type", NpgsqlDbType.Text),
                    new(FactPaths.FsTotalBytes, "total_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.FsUsedBytes, "used_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.FsFreeBytes, "free_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.Derived.FsUsedPercent, "used_pct", NpgsqlDbType.Double),
                ]
            ),
            maxCacheEntries
        ),


        // Key = short container ID (first 12 chars of the 64-char full ID).
        new GenericProjection(
            new ProjectionDef(
                "proj_containers",
                ["Device", "Container"],
                [
                    new(FactPaths.ContainerName, "name", NpgsqlDbType.Text),
                    new(FactPaths.ContainerImage, "image", NpgsqlDbType.Text),
                    new(FactPaths.ContainerState, "state", NpgsqlDbType.Text),
                    new(FactPaths.ContainerHealth, "health", NpgsqlDbType.Text),
                    new(FactPaths.ContainerCpuPercent, "cpu_pct", NpgsqlDbType.Double),
                    new(FactPaths.ContainerMemUsageBytes, "mem_usage_bytes", NpgsqlDbType.Bigint),
                    new(FactPaths.ContainerRestartCount, "restart_count", NpgsqlDbType.Bigint),
                    new(FactPaths.ContainerComposeProject, "compose_project", NpgsqlDbType.Text),
                    new(FactPaths.ContainerComposeService, "compose_service", NpgsqlDbType.Text),
                    new(FactPaths.ContainerRestartPolicy, "restart_policy", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),


        // ── Service identity ──────────────────────────────────────────────────

        new GenericProjection(
            new ProjectionDef(
                "proj_services",
                ["Service"],
                [
                    new(ServicePaths.ServiceId, "service_id", NpgsqlDbType.Text),
                    new(ServicePaths.Type, "type", NpgsqlDbType.Text),
                    new(ServicePaths.DeviceId, "device_id", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // ── CA capability ─────────────────────────────────────────────────────

        new GenericProjection(
            new ProjectionDef(
                "proj_service_ca",
                ["Service"],
                [
                    new(ServicePaths.CaStatus, "ca_status", NpgsqlDbType.Text),
                    new(ServicePaths.CaAddress, "ca_address", NpgsqlDbType.Text),
                    new(ServicePaths.CaRootSubjectDn, "root_subject_dn", NpgsqlDbType.Text),
                    new(ServicePaths.CaRootNotBefore, "root_not_before", NpgsqlDbType.TimestampTz),
                    new(ServicePaths.CaRootNotAfter, "root_not_after", NpgsqlDbType.TimestampTz),
                    new(ServicePaths.CaRootFingerprint, "root_fingerprint", NpgsqlDbType.Text),
                    new(ServicePaths.CaIntermediateSubjectDn, "int_subject_dn", NpgsqlDbType.Text),
                    new(ServicePaths.CaIntermediateNotBefore, "int_not_before", NpgsqlDbType.TimestampTz),
                    new(ServicePaths.CaIntermediateNotAfter, "int_not_after", NpgsqlDbType.TimestampTz),
                ]
            ),
            maxCacheEntries
        ),

        // Key dimensions: Service, Provisioner name
        new GenericProjection(
            new ProjectionDef(
                "proj_service_ca_provisioners",
                ["Service", "Provisioner"],
                [
                    new(ServicePaths.CaProvisionerType, "provisioner_type", NpgsqlDbType.Text),
                    new(ServicePaths.CaProvisionerDefaultDuration, "default_duration", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // X.509 certs found on a device by CertScanCollector. Key = SHA-256 fingerprint —
        // fleet-wide CA rollup (/terrain/ca) groups is_ca=true rows by this to list every CA
        // cert trusted somewhere, and uses is_ca=false rows' issuer_dn to infer CAs we've only
        // ever seen sign a leaf cert.
        new GenericProjection(
            new ProjectionDef(
                "proj_device_certs",
                ["Device", "Cert"],
                [
                    new(FactPaths.CertSubjectDn, "subject_dn", NpgsqlDbType.Text),
                    new(FactPaths.CertIssuerDn, "issuer_dn", NpgsqlDbType.Text),
                    new(FactPaths.CertNotBefore, "not_before", NpgsqlDbType.TimestampTz),
                    new(FactPaths.CertNotAfter, "not_after", NpgsqlDbType.TimestampTz),
                    new(FactPaths.CertPath, "path", NpgsqlDbType.Text),
                    new(FactPaths.CertIsCA, "is_ca", NpgsqlDbType.Boolean),
                    new(FactPaths.CertSANs, "sans", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // Home Assistant device-registry entries have no projection — every column routes to
        // the "Home Assistant Devices" fact view (FactViewLibrary.cs) instead, since nothing
        // queries them cross-service. Resolution/promotion reads the batch's in-memory facts
        // directly (HomeAssistantDevicePromotion), not a projection reread; see
        // docs/plans/ha-inline-discovery.md.

        // ── DNS capability ────────────────────────────────────────────────────

        new GenericProjection(
            new ProjectionDef(
                "proj_dns_stats",
                ["Service"],
                [
                    new(ServicePaths.DnsStatsTotalQueries, "total_queries", NpgsqlDbType.Bigint),
                    new(ServicePaths.DnsStatsTotalBlocked, "total_blocked", NpgsqlDbType.Bigint),
                    new(ServicePaths.DnsStatsBlockedPct, "blocked_pct", NpgsqlDbType.Double),
                ]
            ),
            maxCacheEntries
        ),

        // Key dimensions: Service, Zone name
        new GenericProjection(
            new ProjectionDef(
                "proj_dns_zones",
                ["Service", "Zone"],
                [
                    new(ServicePaths.DnsZoneType, "zone_type", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // Key dimensions: Service, Zone, Record hostname, Record type (A/AAAA/CNAME)
        new GenericProjection(
            new ProjectionDef(
                "proj_dns_records",
                ["Service", "Zone", "Record", "RType"],
                [
                    new(ServicePaths.DnsZoneRecordIP, "ip", NpgsqlDbType.Text),
                    new(ServicePaths.DnsZoneRecordTarget, "target", NpgsqlDbType.Text),
                    new(ServicePaths.DnsZoneRecordTTL, "ttl", NpgsqlDbType.Integer),
                ]
            ),
            maxCacheEntries
        ),

        // ── DHCP capability ───────────────────────────────────────────────────

        // Key dimensions: Service, Scope name
        new GenericProjection(
            new ProjectionDef(
                "proj_dhcp_scopes",
                ["Service", "Scope"],
                [
                    new(ServicePaths.DhcpScopeEnabled, "enabled", NpgsqlDbType.Boolean),
                    new(ServicePaths.DhcpScopeStartAddress, "start_address", NpgsqlDbType.Text),
                    new(ServicePaths.DhcpScopeEndAddress, "end_address", NpgsqlDbType.Text),
                    new(ServicePaths.DhcpScopeSubnetMask, "subnet_mask", NpgsqlDbType.Text),
                    new(ServicePaths.DhcpScopeGateway, "gateway", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // Key dimensions: Service, Scope, Lease (MAC address)
        new GenericProjection(
            new ProjectionDef(
                "proj_dhcp_leases",
                ["Service", "Scope", "Lease"],
                [
                    new(ServicePaths.DhcpLeaseIP, "ip", NpgsqlDbType.Text),
                    new(ServicePaths.DhcpLeaseHostname, "hostname", NpgsqlDbType.Text),
                    new(ServicePaths.DhcpLeaseExpires, "expires_at", NpgsqlDbType.TimestampTz),
                ]
            ),
            maxCacheEntries
        ),

        // ── Device routing ────────────────────────────────────────────────────

        // Key dimensions: Device, Route (destination CIDR)
        new GenericProjection(
            new ProjectionDef(
                "proj_device_routes",
                ["Device", "Route"],
                [
                    new(FactPaths.RouteFamily, "family", NpgsqlDbType.Text),
                    new(FactPaths.RouteGateway, "gateway", NpgsqlDbType.Text),
                    new(FactPaths.RouteInterface, "iface", NpgsqlDbType.Text),
                    new(FactPaths.RouteMetric, "metric", NpgsqlDbType.Integer),
                ]
            ),
            maxCacheEntries
        ),

        // ── Device ARP / neighbor cache ───────────────────────────────────────

        // Key dimensions: Device, ARP (neighbor IP address)
        new GenericProjection(
            new ProjectionDef(
                "proj_device_arp",
                ["Device", "ARP"],
                [
                    new(FactPaths.ArpMac, "mac", NpgsqlDbType.Text),
                    new(FactPaths.ArpInterface, "iface", NpgsqlDbType.Text),
                    new(FactPaths.ArpState, "state", NpgsqlDbType.Text),
                ]
            )
            {
                Indexes =
                [
                    new("ix_proj_device_arp_mac_trgm", ["mac"], ProjectionIndexMethod.GinTrgm),
                    new("ix_proj_device_arp_arp_trgm", ["arp"], ProjectionIndexMethod.GinTrgm),
                ],
            },
            maxCacheEntries
        ),

        // ── Hardware component inventory ─────────────────────────────────────
        //
        // One row per (device, component). A single table covers all component
        // types — local (DIMMs, PCIe, fans, PSUs) and network device inventory
        // (line cards, transceivers, supervisor modules). Type-specific attributes
        // live in the details JSONB column; typed SQL views expose them cleanly.
        //
        // Key = stable component identifier: dmidecode handle, PCI bus address,
        //       SNMP entPhysicalIndex, IPMI sensor name, slot path, etc.
        new GenericProjection(
            new ProjectionDef(
                "proj_hardware_inventory",
                ["Device", "HwComponent"],
                [
                    new(FactPaths.HwComponentClass, "class", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentSlot, "slot", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentDescription, "description", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentVendor, "vendor", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentModel, "model", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentSerial, "serial", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentFirmware, "firmware", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentStatus, "status", NpgsqlDbType.Text),
                    new(FactPaths.HwComponentIsFru, "is_fru", NpgsqlDbType.Boolean),
                ]
            ),
            maxCacheEntries
        ),

        // ── Listening ports ───────────────────────────────────────────────────

        // Key dimension: "proto:addr:port" composite (e.g. "tcp:0.0.0.0:22").
        new GenericProjection(
            new ProjectionDef(
                "proj_ports",
                ["Device", "ListeningPort"],
                [
                    new(FactPaths.PortProtocol, "protocol", NpgsqlDbType.Text),
                    new(FactPaths.PortAddress, "address", NpgsqlDbType.Text),
                    new(FactPaths.PortNumber, "port", NpgsqlDbType.Integer),
                    new(FactPaths.PortProcessName, "process_name", NpgsqlDbType.Text),
                    new(FactPaths.PortPid, "pid", NpgsqlDbType.Bigint),
                ]
            ),
            maxCacheEntries
        ),

        // ── Local DHCP leases ─────────────────────────────────────────────────

        // Key dimension: MAC address. Read from local DHCP server lease files
        // (dnsmasq, ISC dhcpd, Kea, OpenWrt). Distinct from proj_dhcp_leases
        // which is populated by service collectors querying DHCP APIs.
        new GenericProjection(
            new ProjectionDef(
                "proj_dhcp_local_leases",
                ["Device", "Lease"],
                [
                    new(FactPaths.DhcpLocalLeaseIP, "ip", NpgsqlDbType.Text),
                    new(FactPaths.DhcpLocalLeaseHostname, "hostname", NpgsqlDbType.Text),
                    new(FactPaths.DhcpLocalLeaseExpires, "expires_at", NpgsqlDbType.Text),
                    new(FactPaths.DhcpLocalLeaseSource, "source", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

        // BACnet device identity (device_instance, vendor_id, model_name, object_name,
        // firmware_revision, app_software_version, description, location, system_status,
        // serial_number) and Modbus device identity (product_code, revision) + holding/input
        // registers moved to fact views (unification pass) — single-device display only, no
        // cross-device query need. vendor_name from both feeds DeviceVendorDerivation instead
        // (see proj_devices.vendor above); proj_bacnet_device/proj_modbus_device/
        // proj_modbus_holding/proj_modbus_input are dropped entirely (migration).

        // ── Network discovery ─────────────────────────────────────────────────

        // Key dimension: discovered neighbor IP address.
        // Sources is a comma-separated list of scanner names that found this IP.
        new GenericProjection(
            new ProjectionDef(
                "proj_discovered",
                ["Device", "Discovered"],
                [
                    new(FactPaths.DiscoveredMAC, "mac", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredObscuredMAC, "obscured_mac", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredHostname, "hostname", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredSources, "sources", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredOnvifSerial, "onvif_serial", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredRokuSerial, "roku_serial", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredSnmpSerial, "snmp_serial", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredHueBridgeId, "hue_bridge_id", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredOnvifHardwareId, "onvif_hardware_id", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredSsdpUuid, "ssdp_uuid", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredWsdUuid, "wsd_uuid", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredSshHostKey, "ssh_host_key", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredVendor, "vendor", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredModel, "model", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredOs, "os", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredFriendlyName, "friendly_name", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredDeviceType, "device_type", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredCastId, "cast_id", NpgsqlDbType.Text),
                    // Firmware and the sighting/link (observer↔neighbor edge) telemetry moved to the
                    // "Sighting Telemetry" / "Discovered (Probed)" fact views (FactViewLibrary.cs) —
                    // display-only, no cross-device query, no materializer promotion need.
                ]
            ),
            maxCacheEntries
        ),

        // TLS cert presented by a discovered neighbor (TlsCertScanner, via
        // NetworkDiscoveryCollector). The "observed-in-traffic" CA signal on /terrain/ca —
        // read for tls_issuer even though CN/subject/serial are display-only elsewhere.
        new GenericProjection(
            new ProjectionDef(
                "proj_discovered_tls",
                ["Device", "Discovered"],
                [
                    new(FactPaths.DiscoveredTlsSubject, "tls_subject", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredTlsIssuer, "tls_issuer", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredTlsSerial, "tls_serial", NpgsqlDbType.Text),
                    new(FactPaths.DiscoveredTlsNotAfter, "tls_not_after", NpgsqlDbType.TimestampTz),
                ]
            ),
            maxCacheEntries
        ),

        // Advertised services per discovered neighbor (key = service type). A real
        // list dimension rather than a comma-joined string.
        new GenericProjection(
            new ProjectionDef(
                "proj_discovered_services",
                ["Device", "Discovered", "Service"],
                [
                    new(FactPaths.DiscoveredServiceName, "name", NpgsqlDbType.Text),
                ]
            ),
            maxCacheEntries
        ),

    ];
}