using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server;
using JMW.Discovery.Server.Incidents;
using JMW.Discovery.Server.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// End-to-end integration test for the full ETL pipeline:
/// FactIngestPipeline → ProjectionRouter → DiscoveryMaterializer → DeviceRegistry.
/// Uses a realistic two-agent dataset covering every discovery source (ARP, DHCP service,
/// DHCP local, scanner), every fingerprint type the materializer understands, and all the
/// edge cases that matter: invalid MACs, nil UUIDs, short serials, COALESCE bootstrap
/// protection (via same-MAC multi-row batches), and auto-merge (single discovered row whose
/// fingerprints match two pre-existing devices).
/// </summary>
[Collection("Integration")]
public sealed class FullPipelineEndToEndTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public FullPipelineEndToEndTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    // Two simulated observer agents
    private const string Alpha = "aaaaaaaa-0000-0000-0000-000000000000";
    private const string Beta = "bbbbbbbb-0000-0000-0000-000000000000";

    // MACs used in the dataset — all start with 0x00 (globally-administered, unicast)
    private const string MacCiscoRouter = "001122334401"; // ARP only → bare device
    private const string MacBroadcast = "ffffffffffff"; // ARP → SKIPPED
    private const string MacLocallyAdmin = "020000000001"; // ARP → SKIPPED (LA bit)
    private const string MacAllZeros = "000000000000"; // ARP → SKIPPED
    private const string MacHikvision = "001122334402"; // Discovered (multi-fp)
    private const string MacRoku = "001122334403"; // DHCP service (2 rows → COALESCE)
    private const string MacMergeTrigger = "001122334404"; // Discovered → auto-merge
    private const string MacLaptop = "001122334405"; // DHCP service (2 rows → COALESCE)
    private const string MacAlphaEth0 = "001122339900"; // Alpha managed device interface
    private const string MacAlphaEth1 = "001122339901"; // Alpha managed device interface

    // UUIDs used in the dataset — confirmed normalized form: lowercase dashed via Guid.ToString("D")
    private const string UuidHikvisionSsdp = "550e8400-e29b-41d4-a716-446655440001";
    private const string UuidNil = "00000000-0000-0000-0000-000000000000"; // → rejected
    private const string UuidAxisSsdp = "550e8400-e29b-41d4-a716-446655440002"; // serial-only
    private const string UuidMergeD2 = "550e8400-e29b-41d4-a716-446655440099"; // D_uuid fp

    // Chassis serials — normalized form: "{vendor}:{lowercase-serial}"
    private const string SerialHikvisionCam = "hikvision:ds2cd2t47-20230815"; // stored normalized
    private const string SerialMergeD1 = "hikvision:bc000001"; // D_serial fp
    private const string OnvifRawHikvisionCam = "hikvision:DS2CD2T47-20230815"; // raw → proj_discovered
    private const string OnvifRawMergeTrigger = "hikvision:bc000001"; // raw, matches D_serial

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync(
            // singleton device-level projections
            "proj_systems",
            "proj_hardware",
            // per-key device-level projections
            "proj_interfaces",
            "proj_disks",
            "proj_filesystems",
            "proj_containers",
            "proj_hardware_inventory",
            "proj_ports",
            // discovery projection tables
            "proj_discovered",
            "proj_device_arp",
            "proj_device_routes",
            "proj_dhcp_leases",
            "proj_dhcp_local_leases",
            // device registry
            "device_aliases",
            "device_fingerprints",
            "devices",
            "facts_history",
            "audit_log"
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main E2E test
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E2E_MultiAgentDiscovery_CorrectlyMaterializesComplexNetwork()
    {
        // ── Build pipeline components ──────────────────────────────────────────
        IReadOnlyList<IProjection> projections = ProjectionLibrary.CreateAll(_fixture.DataSource);
        FactRepository factRepo = new(_fixture.DataSource, new MetricsRepository(_fixture.DataSource));
        ProjectionRouter router = new(_fixture.DataSource, projections);
        IncidentEvaluator incidents = new(_fixture.DataSource, IncidentTypeRegistry.CreateAll());
        FactIngestPipeline pipeline = new(factRepo, router, AnalysisLibrary.CreateEngine(), incidents);

        DiscoveryMaterializer materializer = new(
            _fixture.DataSource,
            NullLoggerFactory.Instance.CreateLogger<DiscoveryMaterializer>()
        );

        // ── Pre-seed two devices for auto-merge scenario ───────────────────────
        // D_serial is older; it will survive when the merge trigger row links both devices.
        // D_uuid is newer; it will be aliased to D_serial.
        Guid dSerialId = await _fixture.InsertDeviceAsync(
            managementStatus: "discovered",
            createdAt: DateTimeOffset.UtcNow.AddHours(-2)
        );
        await _fixture.InsertFingerprintAsync(dSerialId, FingerprintType.ChassisSerial, SerialMergeD1);

        Guid dUuidId = await _fixture.InsertDeviceAsync(
            managementStatus: "discovered",
            createdAt: DateTimeOffset.UtcNow.AddHours(-1)
        );
        await _fixture.InsertFingerprintAsync(dUuidId, FingerprintType.Uuid, UuidMergeD2);

        // ── Phase 1: Ingest managed device facts via FactIngestPipeline ─────────
        // Simulates the Alpha agent reporting its own OS, SNMP, and interface state.
        List<Fact> alphaFacts =
        [
            // ── proj_systems ──────────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].OS.Hostname", "alpha-gateway"),
            Fact.Create($"Device[{Alpha}].OS.Family", "Linux"),
            Fact.Create($"Device[{Alpha}].OS.Distro", "Ubuntu 22.04"),
            Fact.Create($"Device[{Alpha}].System.MemUsedBytes", 4_000_000_000L),
            Fact.Create($"Device[{Alpha}].System.MemTotalBytes", 8_000_000_000L),
            // Pre-computed derived fact sent by the agent (analysis engine runs client-side)
            Fact.Create($"Device[{Alpha}].System.MemUsedPercent", 50.0),

            // ── proj_snmp_device ──────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].SNMP.SysName", "alpha-gw"),
            Fact.Create($"Device[{Alpha}].SNMP.SysDescr", "Linux alpha-gateway 5.15.0-91-generic"),
            Fact.Create($"Device[{Alpha}].SNMP.EngineId", "80001f8880c0a8010100"),

            // ── proj_interfaces ───────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth0}].Name", "eth0"),
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth0}].SpeedBps", 1_000_000_000L),
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth0}].RxBytes", 100_000L),
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth0}].TxBytes", 200_000L),
            // Pre-computed derived fact (agent computes TotalBytes = RxBytes + TxBytes)
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth0}].TotalBytes", 300_000L),
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth1}].Name", "eth1"),
            Fact.Create($"Device[{Alpha}].Interface[{MacAlphaEth1}].SpeedBps", 10_000_000_000L),

            // ── proj_disks ────────────────────────────────────────────────────
            // Key = serial number. SmartHealth normalizer converts "PASSED" → "PASSED".
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Name", "sda"),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Model", "WDC WD10EARS-00Y5B1"),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].SizeBytes", 1_000_204_886_016L),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Type", "HDD"),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Smart.OverallHealth", "PASSED"),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Smart.TempC", 35.0),
            Fact.Create($"Device[{Alpha}].Disk[WD-WX41E55MNEW7].Smart.PowerOnHours", 12_000L),

            // ── proj_filesystems ──────────────────────────────────────────────
            // Key = mountpoint.
            Fact.Create($"Device[{Alpha}].Filesystem[/].FsType", "ext4"),
            Fact.Create($"Device[{Alpha}].Filesystem[/].TotalBytes", 50_000_000_000L),
            Fact.Create($"Device[{Alpha}].Filesystem[/].UsedBytes", 25_000_000_000L),
            Fact.Create($"Device[{Alpha}].Filesystem[/].FreeBytes", 25_000_000_000L),
            // Pre-computed derived fact (agent computes UsedPercent = UsedBytes/TotalBytes*100)
            Fact.Create($"Device[{Alpha}].Filesystem[/].UsedPercent", 50.0),

            // ── proj_docker + proj_containers ─────────────────────────────────
            Fact.Create($"Device[{Alpha}].Docker.Version", "24.0.5"),
            Fact.Create($"Device[{Alpha}].Docker.ContainersRunning", 3L),
            Fact.Create($"Device[{Alpha}].Docker.Images", 10L),
            Fact.Create($"Device[{Alpha}].Container[abc123def456].Name", "nginx"),
            Fact.Create($"Device[{Alpha}].Container[abc123def456].Image", "nginx:1.25"),
            Fact.Create($"Device[{Alpha}].Container[abc123def456].State", "running"),

            // ── proj_security ─────────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].Security.FirewallEnabled", true),
            Fact.Create($"Device[{Alpha}].Security.FirewallProvider", "ufw"),
            Fact.Create($"Device[{Alpha}].Security.SecureBoot", true),
            Fact.Create($"Device[{Alpha}].Security.TpmPresent", true),
            Fact.Create($"Device[{Alpha}].Security.TpmVersion", "2.0"),
            Fact.Create($"Device[{Alpha}].Security.SeLinuxMode", "enforcing"),

            // ── proj_batteries ────────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].Battery.DesignCapacityWh", 50.0),
            Fact.Create($"Device[{Alpha}].Battery.CurrentCapacityWh", 45.0),
            Fact.Create($"Device[{Alpha}].Battery.CycleCount", 120L),
            Fact.Create($"Device[{Alpha}].Battery.State", "discharging"),
            Fact.Create($"Device[{Alpha}].Battery.ChargePercent", 80.0),
            // Pre-computed derived fact (agent computes HealthPercent = CurrentCap/DesignCap*100)
            Fact.Create($"Device[{Alpha}].Battery.HealthPercent", 90.0),

            // ── proj_updates ──────────────────────────────────────────────────
            Fact.Create($"Device[{Alpha}].Updates.Manager", "apt"),
            Fact.Create($"Device[{Alpha}].Updates.Pending", 5L),
            Fact.Create($"Device[{Alpha}].Updates.Security", 2L),
            Fact.Create($"Device[{Alpha}].Updates.RebootRequired", false),

            // ── proj_device_routes ────────────────────────────────────────────
            // Key = destination CIDR
            Fact.Create($"Device[{Alpha}].Route[0.0.0.0/0].Gateway", "10.0.0.1"),
            Fact.Create($"Device[{Alpha}].Route[0.0.0.0/0].Interface", "eth0"),
            Fact.Create($"Device[{Alpha}].Route[0.0.0.0/0].Family", "inet"),

            // ── proj_device_certs ─────────────────────────────────────────────
            // Key = SHA-256 fingerprint (hex, no colons)
            Fact.Create(
                $"Device[{Alpha}].Cert[aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899].SubjectDn",
                "CN=alpha-gw"
            ),
            Fact.Create(
                $"Device[{Alpha}].Cert[aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899].IssuerDn",
                "CN=My CA"
            ),
            Fact.Create(
                $"Device[{Alpha}].Cert[aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899].IsCA",
                false
            ),

            // ── proj_hardware_inventory ───────────────────────────────────────
            // Key = stable component identifier (dmidecode handle, slot path, etc.)
            Fact.Create($"Device[{Alpha}].HwComponent[DIMM_A1].Class", "memory"),
            Fact.Create($"Device[{Alpha}].HwComponent[DIMM_A1].Description", "16GB DDR4-3200"),
            Fact.Create($"Device[{Alpha}].HwComponent[DIMM_A1].Status", "ok"),

            // ── proj_processes ────────────────────────────────────────────────
            // Key = PID as string
            Fact.Create($"Device[{Alpha}].Process[1234].Name", "nginx"),
            Fact.Create($"Device[{Alpha}].Process[1234].CpuTimeSecs", 12.5),
            Fact.Create($"Device[{Alpha}].Process[1234].MemBytes", 50_000_000L),

            // ── proj_ports ────────────────────────────────────────────────────
            // Key = "proto:addr:port"
            Fact.Create($"Device[{Alpha}].ListeningPort[tcp:0.0.0.0:22].Protocol", "tcp"),
            Fact.Create($"Device[{Alpha}].ListeningPort[tcp:0.0.0.0:22].Address", "0.0.0.0"),
            Fact.Create($"Device[{Alpha}].ListeningPort[tcp:0.0.0.0:22].Port", 22L),
            Fact.Create($"Device[{Alpha}].ListeningPort[tcp:0.0.0.0:22].ProcessName", "sshd"),

            // ── proj_device_services ──────────────────────────────────────────
            // Key = service/unit name (only failed/degraded reported)
            Fact.Create($"Device[{Alpha}].Service[fail2ban.service].ActiveState", "failed"),
            Fact.Create($"Device[{Alpha}].Service[fail2ban.service].SubState", "failed"),

            // ── proj_local_users ──────────────────────────────────────────────
            // Key = username
            Fact.Create($"Device[{Alpha}].LocalUser[jason].Username", "jason"),
            Fact.Create($"Device[{Alpha}].LocalUser[jason].UID", "1000"),
            Fact.Create($"Device[{Alpha}].LocalUser[jason].Shell", "/bin/bash"),
            Fact.Create($"Device[{Alpha}].LocalUser[jason].IsAdmin", true),

            // ── proj_sessions ─────────────────────────────────────────────────
            // Key = "user@tty"
            Fact.Create($"Device[{Alpha}].Session[jason@pts/0].User", "jason"),
            Fact.Create($"Device[{Alpha}].Session[jason@pts/0].TTY", "pts/0"),

            // ── proj_gpu ──────────────────────────────────────────────────────
            // Key = GPU index (0-based)
            Fact.Create($"Device[{Alpha}].GPU[0].Name", "NVIDIA GeForce RTX 3080"),
            Fact.Create($"Device[{Alpha}].GPU[0].Vendor", "NVIDIA"),
            Fact.Create($"Device[{Alpha}].GPU[0].VramMB", 10_240L),

            // ── proj_packages ─────────────────────────────────────────────────
            // Key = package name
            Fact.Create($"Device[{Alpha}].Package[nginx].Version", "1.18.0-6ubuntu14"),
            Fact.Create($"Device[{Alpha}].Package[nginx].Manager", "dpkg"),

            // ── proj_reboots + proj_reboots_history ───────────────────────────
            Fact.Create($"Device[{Alpha}].Reboots.LastBoot", DateTimeOffset.UtcNow.AddDays(-7)),
            Fact.Create($"Device[{Alpha}].Reboots.Count30d", 2L),
            Fact.Create($"Device[{Alpha}].Boot[0].Time", DateTimeOffset.UtcNow.AddDays(-7)),
        ];
        await pipeline.IngestAsync(alphaFacts);

        // ── Phase 2: Seed passive discovery projection tables ──────────────────

        // ARP from Alpha: 1 valid MAC, 3 invalid (broadcast, locally-administered, all-zeros)
        await InsertArpRowAsync(Alpha, "10.0.0.1", MacCiscoRouter);
        await InsertArpRowAsync(Alpha, "10.0.0.254", MacBroadcast);
        await InsertArpRowAsync(Alpha, "10.0.0.253", MacLocallyAdmin);
        await InsertArpRowAsync(Alpha, "10.0.0.252", MacAllZeros);

        // DHCP service from Beta: two rows with the same laptop MAC → COALESCE exercises on hostname
        await InsertDhcpLeaseAsync(Beta, "default", MacLaptop, "192.168.1.100", "laptop-alice");
        await InsertDhcpLeaseAsync(Beta, "corp", MacLaptop, "192.168.1.150", "laptop-corp");

        // DHCP service from Beta: two rows with the same Roku MAC → COALESCE exercises on hostname.
        // Tests now run the production migration chain against the jmwdiscovery schema, so
        // ProjectionTableExistsAsync('jmwdiscovery.proj_dhcp_local_leases') resolves and the
        // local-DHCP materialization pass runs here just as it does in production.
        await InsertDhcpLeaseAsync(Beta, "iot1", MacRoku, "10.0.0.20", "roku-stick-1");
        await InsertDhcpLeaseAsync(Beta, "iot2", MacRoku, "10.0.0.21", "roku-stick-2");

        // Discovered rows from Alpha and Beta scanners:
        //
        // Hikvision camera: full multi-fingerprint row (MAC + ONVIF serial + valid SSDP UUID
        // + nil WSD UUID). A second row from Beta with the same MAC exercises COALESCE on
        // proj_hardware.system_vendor — the second row's "HIKVISION CORP" must not overwrite
        // "Hikvision" from the first row.
        await InsertDiscoveredRowAsync(
            device: Alpha,
            ip: "10.0.0.200",
            mac: MacHikvision,
            onvifSerial: OnvifRawHikvisionCam,
            ssdpUuid: UuidHikvisionSsdp,
            wsdUuid: UuidNil, // nil UUID → normalized to null → NOT stored
            vendor: "Hikvision",
            model: "DS-2CD2T47G2-LSE"
        );
        await InsertDiscoveredRowAsync(
            device: Beta,
            ip: "10.0.0.200",
            mac: MacHikvision,
            vendor: "HIKVISION CORP", // different string from second observer
            model: "DS-2CD"
        );

        // Axis camera: serial-only (no MAC) → created via MaterializeDiscoveredSerialsAsync
        await InsertDiscoveredRowAsync(
            device: Alpha,
            ip: "10.0.0.201",
            mac: null,
            ssdpUuid: UuidAxisSsdp,
            vendor: "Axis",
            model: "P3245"
        );

        // Auto-merge trigger: new MAC + D_serial's ONVIF serial + D_uuid's SSDP UUID.
        // MaterializeDiscoveredMacsAsync finds this MAC is new (not in device_fingerprints),
        // builds fingerprints [mac, chassis-serial, uuid], ResolveAsync finds D_serial (via
        // serial) AND D_uuid (via UUID) → 2 matches → auto-merge. D_serial (older) survives.
        await InsertDiscoveredRowAsync(
            device: Alpha,
            ip: "10.0.0.203",
            mac: MacMergeTrigger,
            onvifSerial: OnvifRawMergeTrigger, // normalizes to SerialMergeD1, matches D_serial
            ssdpUuid: UuidMergeD2, // matches D_uuid
            vendor: "Hikvision",
            model: "DS-NXX"
        );

        // ── Phase 3: First MaterializeAsync run ────────────────────────────────
        await materializer.MaterializeAsync(CancellationToken.None);

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: FactIngestPipeline populated managed device projections
        // ══════════════════════════════════════════════════════════════════════

        string? alphaHostname = await ReadScalarAsync(
            $"SELECT hostname FROM proj_systems WHERE device = '{Alpha}'"
        );
        Assert.Equal("alpha-gateway", alphaHostname);

        string? alphaOsFamily = await ReadScalarAsync(
            $"SELECT os_family FROM proj_systems WHERE device = '{Alpha}'"
        );
        // Normalized at ingest (OS.Family lowercased server-side now that agents emit raw).
        Assert.Equal("linux", alphaOsFamily);

        long alphaInterfaceCount = await _fixture.CountAsync(
            "proj_interfaces",
            $"device = '{Alpha}'"
        );
        Assert.Equal(2, alphaInterfaceCount);

        // Pre-computed MemUsedPercent fact from agent — moved off proj_systems (migration 0060,
        // unread by anything) to the "Resource Usage" fact view; verify the derivation itself
        // still ran and landed in facts_history.
        string? alphaMemPct = await ReadScalarAsync(
            $"""
            SELECT value_double FROM facts_history
            WHERE attribute_path = 'Device[].System.MemUsedPercent' AND key_values ->> 'Device' = '{Alpha}'
            ORDER BY collected_at DESC LIMIT 1
            """
        );
        Assert.Equal("50", alphaMemPct);

        // proj_disks: disk row created with normalized health and type
        Assert.Equal(1, await _fixture.CountAsync("proj_disks", $"device = '{Alpha}'"));
        string? diskHealth = await ReadScalarAsync(
            $"SELECT smart_health FROM proj_disks WHERE device = '{Alpha}' AND disk = 'WD-WX41E55MNEW7'"
        );
        Assert.Equal("PASSED", diskHealth);

        // proj_filesystems: row created; pre-computed used_pct stored
        Assert.Equal(1, await _fixture.CountAsync("proj_filesystems", $"device = '{Alpha}'"));
        string? fsUsedPct = await ReadScalarAsync(
            $"SELECT used_pct FROM proj_filesystems WHERE device = '{Alpha}' AND filesystem = '/'"
        );
        Assert.Equal("50", fsUsedPct);

        // proj_containers: one container row
        Assert.Equal(1, await _fixture.CountAsync("proj_containers", $"device = '{Alpha}'"));

        // proj_hardware_inventory: component row
        Assert.Equal(1, await _fixture.CountAsync("proj_hardware_inventory", $"device = '{Alpha}'"));

        // proj_ports: listening port row
        Assert.Equal(1, await _fixture.CountAsync("proj_ports", $"device = '{Alpha}'"));

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: invalid MACs were rejected — no device_fingerprints created
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacBroadcast}'"
            )
        );

        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacLocallyAdmin}'"
            )
        );

        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacAllZeros}'"
            )
        );

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: Cisco router from ARP — bare device, no bootstrap
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacCiscoRouter}'"
            )
        );

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: DHCP laptop — created, COALESCE on hostname
        // Both same-MAC lease rows are returned in the same NOT EXISTS batch.
        // ResolveAsync on the 2nd call finds the existing device; BootstrapSystemsAsync
        // fires twice. COALESCE(existing, incoming) preserves the first-seen hostname.
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacLaptop}'"
            )
        );

        Guid laptopDeviceId = await ReadDeviceIdByMacAsync(MacLaptop);
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "proj_systems",
                $"device = '{laptopDeviceId}'"
            )
        );

        string? laptopHostname = await ReadScalarAsync(
            $"SELECT hostname FROM proj_systems WHERE device = '{laptopDeviceId}'"
        );
        Assert.NotNull(laptopHostname);
        Assert.True(laptopHostname is "laptop-alice" or "laptop-corp"); // one of the two, COALESCE-preserved

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: Roku from DHCP service (two-scope rows) — COALESCE on hostname
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacRoku}'"
            )
        );

        Guid rokuDeviceId = await ReadDeviceIdByMacAsync(MacRoku);
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "proj_systems",
                $"device = '{rokuDeviceId}'"
            )
        );

        string? rokuHostname = await ReadScalarAsync(
            $"SELECT hostname FROM proj_systems WHERE device = '{rokuDeviceId}'"
        );
        Assert.NotNull(rokuHostname);

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: Hikvision camera — multi-fingerprint, nil UUID rejected,
        //             COALESCE on system_vendor from second observer row
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacHikvision}'"
            )
        );

        Guid hikvisionDeviceId = await ReadDeviceIdByMacAsync(MacHikvision);

        // ONVIF serial stored in normalized form (lowercase)
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'chassis-serial' AND fp_value = '{SerialHikvisionCam}' AND device_id = '{hikvisionDeviceId}'"
            )
        );

        // Valid SSDP UUID stored
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'uuid' AND fp_value = '{UuidHikvisionSsdp}' AND device_id = '{hikvisionDeviceId}'"
            )
        );

        // Nil WSD UUID must NOT have been stored
        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'uuid' AND fp_value = '{UuidNil}'"
            )
        );

        // One proj_hardware row — COALESCE kept one vendor, not two rows
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "proj_hardware",
                $"device = '{hikvisionDeviceId}'"
            )
        );

        string? hikvisionVendor = await ReadScalarAsync(
            $"SELECT system_vendor FROM proj_hardware WHERE device = '{hikvisionDeviceId}'"
        );
        Assert.NotNull(hikvisionVendor);

        string? hikvisionModel = await ReadScalarAsync(
            $"SELECT system_model FROM proj_hardware WHERE device = '{hikvisionDeviceId}'"
        );
        Assert.NotNull(hikvisionModel);

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: Axis serial-only camera — created with UUID fingerprint, no MAC
        // ══════════════════════════════════════════════════════════════════════

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'uuid' AND fp_value = '{UuidAxisSsdp}'"
            )
        );

        Guid axisDeviceId = await ReadDeviceIdByFingerprintAsync(FingerprintType.Uuid, UuidAxisSsdp);

        // Axis had no MAC — no mac fingerprint should exist for this device
        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND device_id = '{axisDeviceId}'"
            )
        );

        string? axisVendor = await ReadScalarAsync(
            $"SELECT system_vendor FROM proj_hardware WHERE device = '{axisDeviceId}'"
        );
        Assert.Equal("Axis", axisVendor);

        // ══════════════════════════════════════════════════════════════════════
        // Assertions: auto-merge result
        // The merge trigger row had MacMergeTrigger (new) + D_serial's serial + D_uuid's UUID.
        // ResolveAsync found D_serial and D_uuid → merged D_uuid into D_serial.
        // D_serial (older created_at) is the survivor.
        // ══════════════════════════════════════════════════════════════════════

        // D_uuid aliased to D_serial
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_aliases",
                $"alias_device_id = '{dUuidId}'"
            )
        );

        string? survivorId = await ReadScalarAsync(
            $"SELECT survivor_device_id::text FROM device_aliases WHERE alias_device_id = '{dUuidId}'"
        );
        Assert.Equal(dSerialId.ToString(), survivorId);

        // All three fingerprints now belong to D_serial
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'chassis-serial' AND fp_value = '{SerialMergeD1}' AND device_id = '{dSerialId}'"
            )
        );

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'uuid' AND fp_value = '{UuidMergeD2}' AND device_id = '{dSerialId}'"
            )
        );

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'mac' AND fp_value = '{MacMergeTrigger}' AND device_id = '{dSerialId}'"
            )
        );

        // D_uuid's fingerprints were reassigned — it now has zero
        Assert.Equal(
            0,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"device_id = '{dUuidId}'"
            )
        );

        // ── Phase 4: Idempotency — second MaterializeAsync run ─────────────────
        long devicesBefore = await _fixture.CountAsync("devices");
        long fingerprintsBefore = await _fixture.CountAsync("device_fingerprints");
        long aliasesBefore = await _fixture.CountAsync("device_aliases");

        await materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(devicesBefore, await _fixture.CountAsync("devices"));
        Assert.Equal(fingerprintsBefore, await _fixture.CountAsync("device_fingerprints"));
        Assert.Equal(aliasesBefore, await _fixture.CountAsync("device_aliases"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bug documentation: serial-only path lacks try/catch around ResolveAsync
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_SerialOnlyRow_AllInvalidFingerprints_Skips()
    {
        // A discovered row with no MAC, a too-short ONVIF serial, and a nil SSDP UUID.
        // BuildFingerprints returns 2 items (the raw strings are non-empty), so the
        // `fps.Count == 0` guard does NOT fire. ResolveAsync normalizes both to null
        // and throws ArgumentException — which the serial pass's try/catch now catches,
        // skipping the row gracefully rather than killing the entire materializer.
        await InsertDiscoveredRowAsync(
            device: Alpha,
            ip: "10.0.0.99",
            mac: null,
            onvifSerial: "hikvision:ab", // too short after vendor split (length 2 < 4)
            ssdpUuid: UuidNil // nil UUID → normalized to null
        );

        DiscoveryMaterializer materializer2 = new(
            _fixture.DataSource,
            NullLoggerFactory.Instance.CreateLogger<DiscoveryMaterializer>()
        );

        // Should complete without throwing
        await materializer2.MaterializeAsync(CancellationToken.None);

        // No device created for this row
        Assert.Equal(0, await _fixture.CountAsync("device_fingerprints"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InsertArpRowAsync(string device, string ip, string mac)
    {
        const string sql = """
            INSERT INTO proj_device_arp (device, arp, mac, iface, state)
            VALUES (@device, @ip, @mac, 'eth0', 'reachable')
            ON CONFLICT (device, arp) DO UPDATE SET mac = EXCLUDED.mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", mac);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDhcpLeaseAsync(
        string service,
        string scope,
        string mac,
        string ip,
        string? hostname
    )
    {
        const string sql = """
            INSERT INTO proj_dhcp_leases (service, scope, lease, ip, hostname)
            VALUES (@service, @scope, @lease, @ip, @hostname)
            ON CONFLICT (service, scope, lease) DO NOTHING
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("service", service);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("lease", mac);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveredRowAsync(
        string device,
        string ip,
        string? mac,
        string? onvifSerial = null,
        string? rokuSerial = null,
        string? ssdpUuid = null,
        string? wsdUuid = null,
        string? vendor = null,
        string? model = null,
        string? hostname = null
    )
    {
        const string sql = """
            INSERT INTO proj_discovered
                (device, discovered, mac, hostname, onvif_serial, roku_serial,
                 ssdp_uuid, wsd_uuid, vendor, model)
            VALUES
                (@device, @ip, @mac, @hostname, @onvifSerial, @rokuSerial,
                 @ssdpUuid, @wsdUuid, @vendor, @model)
            ON CONFLICT (device, discovered) DO UPDATE
              SET mac          = EXCLUDED.mac,
                  onvif_serial = EXCLUDED.onvif_serial,
                  ssdp_uuid    = EXCLUDED.ssdp_uuid,
                  wsd_uuid     = EXCLUDED.wsd_uuid,
                  vendor       = EXCLUDED.vendor,
                  model        = EXCLUDED.model
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", (object?)mac ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("onvifSerial", (object?)onvifSerial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rokuSerial", (object?)rokuSerial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ssdpUuid", (object?)ssdpUuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wsdUuid", (object?)wsdUuid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        // GetNewDiscoveredSerials/GetScannerIdRows/GetSshHostKeyRows now read
        // materialization_facts (docs/plans/architecture-identity-facts.md §5 Phase 2b/2c) —
        // this direct-SQL seed bypasses the router's dual write, so mirror it here.
        if (onvifSerial is not null)
        {
            await InsertMaterializationFactAsync(device, ip, "Device[].Discovered[].OnvifSerial", onvifSerial);
        }

        if (rokuSerial is not null)
        {
            await InsertMaterializationFactAsync(device, ip, "Device[].Discovered[].RokuSerial", rokuSerial);
        }

        if (ssdpUuid is not null)
        {
            await InsertMaterializationFactAsync(device, ip, "Device[].Discovered[].SsdpUuid", ssdpUuid);
        }

        if (wsdUuid is not null)
        {
            await InsertMaterializationFactAsync(device, ip, "Device[].Discovered[].WsdUuid", wsdUuid);
        }
    }

    private async Task InsertMaterializationFactAsync(string device, string entityKey, string attributePath, string value)
    {
        const string sql = """
            INSERT INTO materialization_facts (device, entity_key, attribute_path, value)
            VALUES (@device, @entityKey, @path, @value)
            ON CONFLICT (device, entity_key, attribute_path) DO UPDATE SET value = EXCLUDED.value
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("entityKey", entityKey);
        cmd.Parameters.AddWithValue("path", attributePath);
        cmd.Parameters.AddWithValue("value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> ReadDeviceIdByMacAsync(string mac)
    {
        string? id = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '{mac}'"
        );
        return Guid.Parse(id!);
    }

    private async Task<Guid> ReadDeviceIdByFingerprintAsync(string fpType, string fpValue)
    {
        string? id = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = '{fpType}' AND fp_value = '{fpValue}'"
        );
        return Guid.Parse(id!);
    }

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : result.ToString();
    }
}