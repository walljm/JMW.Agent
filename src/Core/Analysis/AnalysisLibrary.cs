using JMW.Discovery.Core.Analysis.Derivations;
using JMW.Discovery.Core.Analysis.Normalizers;

namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Assembles a pre-configured <see cref="AnalysisEngine" /> for the JMW Agent
/// network discovery system.
/// Normalizers that apply the same logic to multiple fact paths are registered
/// once with all their patterns — no duplicate instances.
/// Derivations are topologically sorted by the engine automatically.
/// </summary>
public static class AnalysisLibrary
{
    public static AnalysisEngine CreateEngine() => new(
        normalizers:
        [
            // ── Network ───────────────────────────────────────────────────────
            new MacValueNormalizer(), // all MAC values (interface/perm/ARP/discovered) → bare hex (match fingerprints)
            new IpAddressNormalizer(), // IP values (interface v4/v6, DHCP-local lease, listening port) → canonical
            new InterfaceNameNormalizer(),
            new SpeedNormalizer(),

            // ── Discovered device identity — match device_fingerprints.fp_value format ─
            new SerialValueNormalizer(), // ONVIF/Roku serial → "bare:<value>" (match chassis-serial fingerprints)
            new UuidValueNormalizer(), // SSDP/WS-Discovery UUID → canonical lowercase GUID (match uuid fingerprints)

            // ── Hostname (pipeline: lowercase → strip trailing dot → reject empty) ─
            HostnameNormalizer.Create(),

            // ── Lowercase + trim — all enum-like string fields ────────────────
            new LowercaseTrimNormalizer(
                [
                    FactPaths.SystemOsFamily,
                    FactPaths.HwVirtualization,
                    FactPaths.DeviceKind,
                    FactPaths.ContainerState,
                    FactPaths.ContainerHealth,
                    FactPaths.BatteryState,
                    FactPaths.InterfaceDuplex,
                    FactPaths.InterfaceType,
                ]
            ),

            // ── Vendor/manufacturer names — canonical proper-case display form,
            // NOT lowercased (see VendorNormalizer for why) ────────────────────
            new VendorNormalizer(),

            // ── OS distro — canonical display form, NOT lowercased (see OsDistroNormalizer) ───
            new OsDistroNormalizer(),

            // ── Model strings — whitespace/placeholder cleanup only (see ModelNormalizer) ────
            new ModelNormalizer(
                [
                    FactPaths.HwSystemModel,
                    FactPaths.HwBoardModel,
                    FactPaths.DiscoveredModel,
                    FactPaths.BacnetModelName,
                    ServicePaths.HomeAssistantHaDeviceModel,
                    FactPaths.HwComponentModel,
                ]
            ),

            // ── Disk / filesystem ──────────────────────────────────────────────
            new DiskTypeNormalizer(),
            new FsTypeNormalizer(),
            new SmartHealthNormalizer(),

            // ── Clamp percentages to [0, 100] ─────────────────────────────────
            new ClampPercentNormalizer(
                [
                    FactPaths.SystemCpuPercent,
                    FactPaths.ContainerCpuPercent,
                    FactPaths.BatteryChargePercent,
                    FactPaths.DiskSmartWearPercent,
                ]
            ),

            // ── Non-negative bytes (zero OK) ──────────────────────────────────
            new NonNegativeBytesNormalizer(
                [
                    FactPaths.SystemMemUsedBytes,
                    FactPaths.FsUsedBytes,
                    FactPaths.FsFreeBytes,
                ]
            ),

            // ── Non-negative bytes, zero rejected (denominator fields) ─────────
            // These are used in derived percent calculations; a zero total would
            // produce division-by-zero in derivations.
            new NonNegativeBytesNormalizer(
                [
                    FactPaths.SystemMemTotalBytes,
                    FactPaths.HwTotalMemBytes,
                    FactPaths.FsTotalBytes,
                    FactPaths.DiskSizeBytes,
                ],
                rejectZero: true
            ),
        ],
        derivations:
        [
            new TotalBytesDerivation(),
            new UsedPercentDerivation(),
            new MemoryUsedPercentDerivation(),
            new BatteryHealthDerivation(),
            new DeviceVendorDerivation(),
            new VendorFromOsDistroDerivation(),
            new VendorFromSnmpSysDescrDerivation(),
            new OsFromSnmpSysDescrDerivation(),
            new VendorFromModelPrefixDerivation(),
            new VendorFromHostnamePrefixDerivation(),
            new DeviceKindDerivation(),
        ]
    );
}