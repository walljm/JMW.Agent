using JMW.Discovery.Core;
using JMW.Discovery.Server;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Integration tests for DiscoveryMaterializer: verifies that ARP, DHCP, and
/// discovered-neighbor rows drive device creation and bootstrap fact writes.
/// </summary>
[Collection("Integration")]
public sealed class DiscoveryMaterializerTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;

    public DiscoveryMaterializerTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private DiscoveryMaterializer _materializer = null!;

    public Task InitializeAsync()
    {
        _materializer = new DiscoveryMaterializer(
            _fixture.DataSource,
            NullLoggerFactory.Instance.CreateLogger<DiscoveryMaterializer>()
        );
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.TruncateAsync(
            "proj_systems",
            "proj_hardware",
            "proj_devices",
            "proj_discovered",
            "proj_interfaces",
            "proj_device_arp",
            "proj_dhcp_leases",
            "proj_dhcp_local_leases",
            "materialization_facts",
            "device_aliases",
            "device_fingerprints",
            "devices"
        );
    }

    // ── ARP materialization ───────────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_ArpMac_CreatesDevice()
    {
        // Seed an ARP entry with a new MAC that has no matching device.
        await InsertArpRowAsync("observer-device-1", "192.168.1.100", "001122334455", "eth0");

        await _materializer.MaterializeAsync(CancellationToken.None);

        // A device should now exist with the ARP MAC as its fingerprint.
        long fpCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334455'"
        );
        Assert.Equal(1, fpCount);
    }

    [Fact]
    public async Task MaterializeAsync_ArpMac_SecondRun_DoesNotDuplicate()
    {
        await InsertArpRowAsync("observer-device-1", "192.168.1.101", "001122334466", "eth0");

        // Run twice — second run should find the fingerprint already registered.
        await _materializer.MaterializeAsync(CancellationToken.None);
        await _materializer.MaterializeAsync(CancellationToken.None);

        long devCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334466'"
        );
        Assert.Equal(1, devCount);
    }

    [Fact]
    public async Task MaterializeAsync_InvalidArpMac_Skipped()
    {
        // Broadcast MAC is invalid — should be silently skipped.
        await InsertArpRowAsync("observer-device-1", "192.168.1.254", "ffffffffffff", "eth0");

        await _materializer.MaterializeAsync(CancellationToken.None);

        long devCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = 'ffffffffffff'"
        );
        Assert.Equal(0, devCount);
    }

    // GetNewArpMacs.sql's anti-join must correlate against THIS row's mac, not any row in
    // fingerprinted_macs — an unqualified reference inside the correlated subquery can silently
    // bind to the subquery's own column instead (Postgres resolves unqualified names from the
    // innermost scope outward), which degenerates the correlation into "does fingerprinted_macs
    // have any row at all", making every ARP row look already-fingerprinted the moment ANY MAC
    // anywhere has one. MaterializeAsync_ArpMac_SecondRun_DoesNotDuplicate above re-checks the
    // SAME mac, which that bug also happens to pass — this seeds an unrelated mac first
    // specifically to catch it.
    [Fact]
    public async Task MaterializeAsync_ArpMac_UnrelatedMacAlreadyFingerprinted_StillDetectsNewMac()
    {
        await InsertArpRowAsync("observer-device-1", "192.168.1.102", "001122334477", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None); // fingerprints the first mac

        await InsertArpRowAsync("observer-device-2", "192.168.1.103", "001122334488", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None); // must still detect the second

        long devCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334488'"
        );
        Assert.Equal(1, devCount);
    }

    // ── Discovered MACs ───────────────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_DiscoveredMac_CreatesDeviceAndBootstrapsProfile()
    {
        await InsertDiscoveredRowAsync(
            observer: "observer-device-1",
            ip: "10.0.0.50",
            mac: "001122334477",
            hostname: "camera-1",
            vendor: "Acme",
            model: "IPCam500"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // Device should exist.
        long fpCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334477'"
        );
        Assert.Equal(1, fpCount);

        // Hostname should be bootstrapped into proj_systems.
        string? hostname = await ReadScalarAsync(
            """
            SELECT s.hostname
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122334477'
            """
        );
        Assert.Equal("camera-1", hostname);

        // Vendor/model should be bootstrapped into proj_hardware.
        string? vendor = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM proj_hardware h
            JOIN device_fingerprints f ON f.device_id = h.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122334477'
            """
        );
        Assert.Equal("Acme", vendor);
    }

    [Fact]
    public async Task MaterializeAsync_DiscoveredOs_PromotesToOsFamily()
    {
        await InsertDiscoveredRowAsync(
            observer: "observer-os-1",
            ip: "10.0.0.51",
            mac: "0011223344AA",
            hostname: "router-1",
            os: "RouterOS"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The discovered OS (from HTTP fingerprinting) should promote to proj_systems.os_family.
        string? osFamily = await ReadScalarAsync(
            """
            SELECT s.os_family
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '0011223344aa'
            """
        );
        Assert.Equal("RouterOS", osFamily);
    }

    [Fact]
    public async Task MaterializeAsync_DiscoveredMac_ExistingDevice_MergesFingerprint()
    {
        // Pre-create a device with the MAC already registered.
        Guid existingId = await _fixture.InsertDeviceAsync(managementStatus: "discovered");
        await _fixture.InsertFingerprintAsync(existingId, FingerprintType.Mac, "001122334488");

        // Seed a discovered row for the same MAC — should not create a duplicate.
        await InsertDiscoveredRowAsync(
            observer: "observer-device-1",
            ip: "10.0.0.51",
            mac: "001122334488",
            hostname: "known-cam"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        long devCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334488'"
        );
        Assert.Equal(1, devCount);
    }

    // ── DHCP materialization ──────────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_DhcpLease_CreatesDevice()
    {
        await InsertDhcpLeaseAsync(
            service: "dhcp-server-1",
            mac: "001122334499",
            ip: "192.168.1.50",
            hostname: "laptop-1"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        long fpCount = await _fixture.CountAsync(
            "device_fingerprints",
            "fp_type = 'mac' AND fp_value = '001122334499'"
        );
        Assert.Equal(1, fpCount);
    }

    [Fact]
    public async Task MaterializeAsync_DhcpLocalLease_CreatesDeviceAndPromotesIp()
    {
        // The dhcp-local source: resolve by MAC and promote the lease IP into last_seen_ip.
        await InsertDhcpLocalLeaseAsync("router-1", mac: "0011223344aa", ip: "192.168.1.60", hostname: "nas-1");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(
            "nas-1",
            await ReadScalarAsync(
                """
            SELECT s.hostname FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '0011223344aa'
            """
            )
        );
        Assert.Equal(
            "192.168.1.60",
            await ReadScalarAsync(
                """
            SELECT s.last_seen_ip FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '0011223344aa'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_DiscoveredSerial_NoMac_ResolvesByUuidAndPromotes()
    {
        // The scanner-serial source: a device with no MAC but a UUID resolves on the UUID
        // fingerprint, and its hostname/vendor/model are promoted.
        await InsertDiscoveredUuidRowAsync(
            "observer-device-1",
            "192.168.1.70",
            "550e8400-e29b-41d4-a716-446655440000",
            hostname: "printer-7",
            vendor: "Acme",
            model: "LaserJet"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        string? viaUuid = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'uuid' AND fp_value = '550e8400-e29b-41d4-a716-446655440000'"
        );
        Assert.NotNull(viaUuid);
        Assert.Equal(
            "printer-7",
            await ReadScalarAsync($"SELECT hostname FROM proj_systems WHERE device = '{viaUuid}'")
        );
        Assert.Equal(
            "Acme",
            await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{viaUuid}'")
        );
        Assert.Equal(
            "LaserJet",
            await ReadScalarAsync($"SELECT system_model FROM proj_hardware WHERE device = '{viaUuid}'")
        );
    }

    [Fact]
    public async Task MaterializeAsync_DiscoveredSnmpSerial_NoMac_ResolvesAndPromotesHardwareSerial()
    {
        // Regression guard: Device[].Discovered[].SnmpSerial (Printer-MIB prtGeneralSerialNumber)
        // must follow the same promotion path as OnvifSerial/RokuSerial — resolved as a
        // ChassisSerial fingerprint and promoted onto proj_hardware.system_serial, which is what
        // the device details/hardware page displays as the hardware serial number.
        //
        // The row is seeded with the value already in the "bare:<lowercase>" form
        // SerialValueNormalizer writes at ingest time (proj_discovered always holds that form in
        // production) — DeviceRegistry's fingerprint-resolution normalization is idempotent over it.
        await InsertDiscoveredSnmpSerialRowAsync(
            "observer-device-1",
            "192.168.1.75",
            "bare:sn-printer-0042",
            hostname: "printer-lobby",
            vendor: "Acme",
            model: "LaserJet"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        string? viaSerial = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'chassis-serial' AND fp_value = 'bare:sn-printer-0042'"
        );
        Assert.NotNull(viaSerial);
        Assert.Equal(
            "bare:sn-printer-0042",
            await ReadScalarAsync($"SELECT system_serial FROM proj_hardware WHERE device = '{viaSerial}'")
        );
        Assert.Equal(
            "Acme",
            await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{viaSerial}'")
        );
    }

    // ── Obscured-MAC reconstruction (Google Wifi) ─────────────────────────────

    [Fact]
    public async Task MaterializeAsync_ObscuredMac_ReconstructedByIpAndOui()
    {
        // Google Wifi reports IP .60 with an obscured MAC that preserves only the OUI
        // (00e0bf…). Core's ARP knows the real MAC for .60, and its OUI matches, so
        // the IP join + OUI corroboration reconstructs the full MAC → one device.
        await InsertArpRowAsync("observer-device-1", "192.168.1.60", "00e0bf400073", "eth0");
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.60", "00e0bf1fc40*", "jasons-phone");

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The discovered row's mac was populated with the IP-attested real MAC.
        Assert.Equal(
            "00e0bf400073",
            await ReadScalarAsync(
                "SELECT mac FROM proj_discovered WHERE device = 'google-wifi-ap' AND discovered = '192.168.1.60'"
            )
        );
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '00e0bf400073'")
        );

        // The obscured MAC is anchored as a fingerprint on the SAME device as the
        // reconstructed real MAC, so future OnHub reports that can't reconstruct still
        // coalesce with this hardware instead of minting a duplicate.
        Assert.Equal(
            "00e0bf1fc40*",
            await ReadScalarAsync(
                """
            SELECT f2.fp_value
            FROM device_fingerprints f1
            JOIN device_fingerprints f2 ON f2.device_id = f1.device_id AND f2.fp_type = 'obscured-mac'
            WHERE f1.fp_type = 'mac' AND f1.fp_value = '00e0bf400073'
            """
            )
        );

        // The station's hostname bootstraps onto the reconstructed device.
        Assert.Equal(
            "jasons-phone",
            await ReadScalarAsync(
                """
            SELECT s.hostname
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '00e0bf400073'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_ObscuredMac_NoKnownIp_MintsDeviceOnObscuredMac()
    {
        // The server knows no real MAC for this IP, so the '*' form can't reconstruct
        // (mac column stays null). But the obscured MAC reveals 11 of 12 nibbles and its OUI is
        // globally-unique (0x3c — not randomized), so it is a stable anchor in its own right: it
        // mints a device carrying the station's mDNS hostname, surfacing a host that would
        // otherwise be invisible.
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.61", "3c22fbaabb0*", "ghost");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Null(
            await ReadScalarAsync(
                "SELECT mac FROM proj_discovered WHERE device = 'google-wifi-ap' AND discovered = '192.168.1.61'"
            )
        );
        Assert.Equal(
            "3c22fbaabb0*",
            await ReadScalarAsync(
                "SELECT obscured_mac FROM proj_discovered WHERE device = 'google-wifi-ap' AND discovered = '192.168.1.61'"
            )
        );

        // A device is minted, anchored on the obscured MAC, carrying the hostname.
        Assert.Equal(
            "ghost",
            await ReadScalarAsync(
                """
            SELECT s.hostname
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'obscured-mac' AND f.fp_value = '3c22fbaabb0*'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_ObscuredMac_LocallyAdministered_MintsNoDevice()
    {
        // A randomized Wi-Fi MAC (Apple "Private Wi-Fi Address" etc.) is seen only by its obscured
        // form, whose OUI carries the locally-administered bit (0x3a → 0x02 set). It is NOT a stable
        // identity, so it must mint NO device — the sighting is kept as an observation only.
        // Without this, every MAC rotation spawns a fresh phantom device (the bug that split one
        // MacBook into two records: real OUI 64:4b:f0 vs randomized 3a:91:b0).
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.62", "3a91b0b6466*", "roaming-laptop");

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The observation is preserved …
        Assert.Equal(
            "3a91b0b6466*",
            await ReadScalarAsync(
                "SELECT obscured_mac FROM proj_discovered WHERE device = 'google-wifi-ap' AND discovered = '192.168.1.62'"
            )
        );
        // … but no device was minted from the randomized MAC.
        Assert.Equal(
            0,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'obscured-mac' AND fp_value = '3a91b0b6466*'")
        );
    }

    [Fact]
    public async Task MaterializeAsync_ObscuredMac_OuiMismatch_Rejected()
    {
        // The IP maps (in stale ARP) to a different-vendor MAC than the obscured OUI —
        // a reassigned IP. OUI corroboration must reject it: no reconstruction, and the
        // ARP device must NOT inherit the Google Wifi hostname.
        await InsertArpRowAsync("observer-device-1", "192.168.1.207", "5c475edf3e42", "eth0");
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.207", "ccf41158097*", "wrong-ip");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Null(
            await ReadScalarAsync(
                "SELECT mac FROM proj_discovered WHERE device = 'google-wifi-ap' AND discovered = '192.168.1.207'"
            )
        );
        // The ARP device exists but did not pick up the mismatched station's hostname.
        Assert.Null(
            await ReadScalarAsync(
                """
            SELECT s.hostname
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '5c475edf3e42'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_ObscuredMac_LocallyAdministeredReconstruction_DoesNotAbortOtherRows()
    {
        // A randomized/locally-administered MAC (first octet bit-1 set, e.g. 8a…) can
        // be reconstructed from ARP but is rejected as a fingerprint. It must not abort
        // the pass: a later, valid station still reconstructs and promotes.
        await InsertArpRowAsync("observer-device-1", "192.168.1.10", "8a32a4377887", "eth0"); // locally administered
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.10", "8a32a4c2df3*", "randomized-phone");

        await InsertArpRowAsync("observer-device-1", "192.168.1.60", "00e0bf400073", "eth0"); // valid
        await InsertObscuredDiscoveredRowAsync("google-wifi-ap", "192.168.1.60", "00e0bf1fc40*", "good-device");

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The randomized MAC minted no device (rejected fingerprint).
        Assert.Equal(
            0,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '8a32a4377887'")
        );
        // The valid device still resolved + inherited its hostname despite the earlier throw.
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '00e0bf400073'")
        );
        Assert.Equal(
            "good-device",
            await ReadScalarAsync(
                """
            SELECT s.hostname
            FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '00e0bf400073'
            """
            )
        );
    }

    // ── Cast-id identity (stale mDNS on a reused IP) ───────────────────────────

    [Fact]
    public async Task MaterializeAsync_CastId_TwoMacs_NameDoesNotSmearOntoWrongHardware()
    {
        // One Nest Mini (cast id 5bda1a…) whose stale mDNS advertisement lingers on an
        // old IP (.214, now a Ring device) while it currently lives at .235 (Google).
        // Both discovered rows carry the SAME cast id but reconstruct to DIFFERENT MACs.
        // The Google friendly name must bind to the cast-id device, NOT to the Ring MAC.
        const string castId = "5bda1ab442cfad53ade4f0703c261715";
        await InsertArpRowAsync("observer-device-1", "192.168.1.214", "187f881bcdb1", "eth0"); // Ring
        await InsertArpRowAsync("observer-device-1", "192.168.1.235", "ccf411bc9fca", "eth0"); // Google
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.214",
            "187f889c4e6*",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.235",
            "ccf411bc9fc*",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // Both MACs reconstruct as their own hardware devices…
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = '187f881bcdb1'")
        );
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = 'ccf411bc9fca'")
        );

        // …but NEITHER MAC device carries the cast-id fingerprint (name not smeared onto hardware).
        Assert.Null(
            await ReadScalarAsync(
                $"""
            SELECT f2.fp_value
            FROM device_fingerprints f1
            JOIN device_fingerprints f2 ON f2.device_id = f1.device_id AND f2.fp_type = 'cast-id'
            WHERE f1.fp_type = 'mac' AND f1.fp_value = '187f881bcdb1'
            """
            )
        );

        // The Ring MAC device did NOT inherit the Google speaker's name/kind.
        Assert.Null(
            await ReadScalarAsync(
                """
            SELECT s.hostname FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '187f881bcdb1'
            """
            )
        );

        // The cast-id device exists and carries the friendly name (not a real hostname — the
        // mDNS friendly name is display-only).
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", $"fp_type = 'cast-id' AND fp_value = '{castId}'")
        );
        Assert.Equal(
            "Mother In Law Suite speaker",
            await ReadScalarAsync(
                $"""
            SELECT s.friendly_name FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'cast-id' AND f.fp_value = '{castId}'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_CastId_OneMacReconstructs_SpeakerUnifiesAndOtherStaysNameless()
    {
        // The real-world shape from live data: two DIFFERENT physical devices share a
        // cast id in Google's mDNS. The actual speaker (.214) has an obscured MAC that
        // no other observer corroborates (no reconstruction). The other device (.235)
        // reconstructs to a real MAC a scanner independently sees. The speaker must
        // unify (obscured MAC + cast id + name) into ONE record, while the other device
        // stays a separate, name-less device — no smear onto it, no fragmenting of the
        // speaker.
        const string castId = "5bda1ab442cfad53ade4f0703c261715";
        // .235 reconstructs to a real Google MAC an ARP observer also sees.
        await InsertArpRowAsync("observer-device-1", "192.168.1.235", "ccf411bc9fca", "eth0");
        // .214 is the actual speaker: obscured MAC, but no ARP corroboration → no reconstruction.
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.214",
            "187f889c4e6*",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.235",
            "ccf411bc9fc*",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The .235 real-MAC device is separate: it carries neither the cast id nor the name.
        Assert.Equal(
            1,
            await _fixture.CountAsync("device_fingerprints", "fp_type = 'mac' AND fp_value = 'ccf411bc9fca'")
        );
        Assert.Null(
            await ReadScalarAsync(
                """
            SELECT f2.fp_value
            FROM device_fingerprints f1
            JOIN device_fingerprints f2 ON f2.device_id = f1.device_id AND f2.fp_type = 'cast-id'
            WHERE f1.fp_type = 'mac' AND f1.fp_value = 'ccf411bc9fca'
            """
            )
        );
        Assert.Null(
            await ReadScalarAsync(
                """
            SELECT s.hostname FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = 'ccf411bc9fca'
            """
            )
        );

        // The speaker is ONE record: cast id, the .214 obscured MAC, and the name all
        // live on the same device.
        Assert.Equal(
            "187f889c4e6*",
            await ReadScalarAsync(
                $"""
            SELECT f2.fp_value
            FROM device_fingerprints f1
            JOIN device_fingerprints f2 ON f2.device_id = f1.device_id AND f2.fp_type = 'obscured-mac'
            WHERE f1.fp_type = 'cast-id' AND f1.fp_value = '{castId}'
            """
            )
        );
        Assert.Equal(
            "Mother In Law Suite speaker",
            await ReadScalarAsync(
                $"""
            SELECT s.friendly_name FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'cast-id' AND f.fp_value = '{castId}'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_CastId_CurrentRowHasNoObscuredMac_StillNotSmeared()
    {
        // The real live shape: the CURRENT device (.235) carries the cast id via
        // networkState only — NO obscured_mac — so it is absent from GetObscuredMacRows.
        // The STALE row (.214, now a Ring device) DOES have an obscured MAC. The IP
        // count must be taken over the full proj_discovered (2 IPs) — not the
        // obscured-MAC subset (which would see only .214 → 1 → wrongly co-register the
        // cast name onto the Ring MAC).
        const string castId = "5bda1ab442cfad53ade4f0703c261715";
        await InsertArpRowAsync("observer-device-1", "192.168.1.214", "187f881bcdb1", "eth0"); // Ring, at the stale IP
        // Stale row: obscured MAC present (in ap-show) → reconstructs to the Ring MAC.
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.214",
            "187f889c4e6*",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );
        // Current row: cast id but NO obscured MAC (networkState-only).
        await InsertCastDiscoveredNoObscuredRowAsync(
            "google-wifi-ap",
            "192.168.1.235",
            castId,
            "Mother In Law Suite speaker",
            "Google-Nest-Mini"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The Ring MAC device must NOT carry the cast-id fingerprint nor the name.
        Assert.Null(
            await ReadScalarAsync(
                $"""
            SELECT f2.fp_value
            FROM device_fingerprints f1
            JOIN device_fingerprints f2 ON f2.device_id = f1.device_id AND f2.fp_type = 'cast-id'
            WHERE f1.fp_type = 'mac' AND f1.fp_value = '187f881bcdb1'
            """
            )
        );
        Assert.Null(
            await ReadScalarAsync(
                """
            SELECT s.hostname FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'mac' AND f.fp_value = '187f881bcdb1'
            """
            )
        );
        // The cast-id device carries the name.
        Assert.Equal(
            "Mother In Law Suite speaker",
            await ReadScalarAsync(
                $"""
            SELECT s.friendly_name FROM proj_systems s
            JOIN device_fingerprints f ON f.device_id = s.device::uuid
            WHERE f.fp_type = 'cast-id' AND f.fp_value = '{castId}'
            """
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_CastId_SingleMac_CoRegistersMacAndCastId()
    {
        // Normal single-IP cast device: cast id maps to exactly one MAC → the device is
        // co-registered under BOTH the cast id and the MAC, and gets the friendly name.
        const string castId = "1294150e88618bcc369e24bf70d0c24a";
        await InsertArpRowAsync("observer-device-1", "192.168.1.211", "d88c79420abf", "eth0");
        await InsertCastDiscoveredRowAsync(
            "google-wifi-ap",
            "192.168.1.211",
            "d88c79cb538*",
            castId,
            "Kitchen Audio",
            "Nest-Audio"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // Same device carries both fingerprints.
        string? viaMac = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = 'd88c79420abf'"
        );
        string? viaCast = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'cast-id' AND fp_value = '{castId}'"
        );
        Assert.NotNull(viaMac);
        Assert.Equal(viaMac, viaCast);

        Assert.Equal(
            "Kitchen Audio",
            await ReadScalarAsync(
                "SELECT friendly_name FROM proj_systems WHERE device = '" + viaMac + "'"
            )
        );
        Assert.Equal(
            "Nest-Audio",
            await ReadScalarAsync(
                "SELECT kind FROM proj_devices WHERE device = '" + viaMac + "'"
            )
        );
    }

    // ── SSH host-key identity ─────────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_SshHostKey_SameKeyDifferentMacs_MergeToOneDevice()
    {
        // The same host seen at two IPs with two different MACs, but the SAME SSH
        // host-key. The MAC passes create two devices; the host-key pass must merge them
        // into one (the host key is a stable per-host identity).
        const string hostKey = "sha256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";
        await InsertSshDiscoveredRowAsync("observer-device-1", "192.168.1.10", "001122334401", hostKey);
        await InsertSshDiscoveredRowAsync("observer-device-1", "192.168.1.11", "001122334402", hostKey);

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The host-key is registered exactly once…
        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'ssh-host-key' AND fp_value = '{hostKey}'"
            )
        );

        // …and both MAC devices are now the SAME device as the host-key device.
        string? viaMac1 = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122334401'"
        );
        string? viaMac2 = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '001122334402'"
        );
        string? viaKey = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'ssh-host-key' AND fp_value = '{hostKey}'"
        );
        Assert.NotNull(viaMac1);
        Assert.Equal(viaMac1, viaMac2);
        Assert.Equal(viaMac1, viaKey);
    }

    [Fact]
    public async Task MaterializeAsync_SshHostKey_NoMac_MintsDeviceOnHostKey()
    {
        // An SSH host with no MAC in scanner data still mints a device anchored on its
        // host-key, surfacing a host that would otherwise be invisible.
        const string key = "sha256:LP2Z6l3aNfMbY0hVxqJ8kQnR5tW7cD1eG4iH9oP0uS3";
        await InsertSshDiscoveredRowAsync("observer-device-1", "192.168.1.20", null, key);

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(
            1,
            await _fixture.CountAsync(
                "device_fingerprints",
                $"fp_type = 'ssh-host-key' AND fp_value = '{key}'"
            )
        );
    }

    // Home Assistant device-registry promotion moved out of DiscoveryMaterializer — it now
    // resolves inline from the ingest batch's own facts (HomeAssistantDevicePromotion); see
    // HomeAssistantDevicePromotionTests.cs and docs/plans/ha-inline-discovery.md.

    // ── Obscured interface-MAC reconstruction (Google Wifi AP) ────────────────

    [Fact]
    public async Task MaterializeAsync_InterfaceObscuredMac_ReconstructedByIpAndOui()
    {
        // The AP reports its own br-lan interface with an obscured MAC (OUI 703acb).
        // Another agent's ARP knows the real MAC for that gateway IP, OUI matches, so
        // the interface's mac_address is filled with the real (colon-formatted) MAC.
        await InsertArpRowAsync("observer-device-1", "192.168.1.1", "703acb70d073", "eth0");
        await InsertInterfaceObscuredRowAsync("google-wifi-ap", "br-lan", "192.168.1.1", "703acb70d06*");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(
            "70:3A:CB:70:D0:73",
            await ReadScalarAsync(
                "SELECT mac_address FROM proj_interfaces WHERE device = 'google-wifi-ap' AND interface = 'br-lan'"
            )
        );
        // The obscured value is retained as the raw kept fact.
        Assert.Equal(
            "703acb70d06*",
            await ReadScalarAsync(
                "SELECT obscured_mac FROM proj_interfaces WHERE device = 'google-wifi-ap' AND interface = 'br-lan'"
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_InterfaceObscuredMac_NoKnownIp_LeavesMacNull()
    {
        // No source attests a real MAC for the WAN's public IP → no reconstruction,
        // and the obscured value is never written as the mac_address.
        await InsertInterfaceObscuredRowAsync("google-wifi-ap", "wan0", "173.67.196.15", "703acb1f8f8*");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Null(
            await ReadScalarAsync(
                "SELECT mac_address FROM proj_interfaces WHERE device = 'google-wifi-ap' AND interface = 'wan0'"
            )
        );
        Assert.Equal(
            "703acb1f8f8*",
            await ReadScalarAsync(
                "SELECT obscured_mac FROM proj_interfaces WHERE device = 'google-wifi-ap' AND interface = 'wan0'"
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_InterfaceObscuredMac_OuiMismatch_Rejected()
    {
        // A stale ARP entry maps the IP to a different-vendor MAC than the obscured OUI.
        // OUI corroboration must reject it: mac_address stays null.
        await InsertArpRowAsync("observer-device-1", "192.168.1.1", "5c475edf3e42", "eth0");
        await InsertInterfaceObscuredRowAsync("google-wifi-ap", "br-lan", "192.168.1.1", "703acb70d06*");

        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Null(
            await ReadScalarAsync(
                "SELECT mac_address FROM proj_interfaces WHERE device = 'google-wifi-ap' AND interface = 'br-lan'"
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_InterfaceObscuredMac_MergesApWithScannerDeviceOnReconstructedMac()
    {
        // The AP already exists as its own device (keyed by its google-wifi-device-id),
        // and a scanner has separately recorded the AP's real br-lan MAC as another
        // device. Reconstructing the interface's obscured MAC and unioning it with the
        // AP's own identity must MERGE the two into one device — the fix for an AP that
        // otherwise shows up twice. (proj_interfaces.device is the resolved Guid device
        // id in production, so the merge branch — guarded on Guid.TryParse — runs.)
        const string apGwId = "abcdef0123456789"; // hex ≥8 → survives normalization
        const string realMac = "703acb70d073";

        // A scanner recorded the AP's real MAC as its own device.
        await InsertArpRowAsync("observer-device-1", "192.168.1.1", realMac, "eth0");

        // The pre-existing AP device, keyed by its google-wifi-device-id (Guid id).
        DeviceRegistry registry = new(_fixture.DataSource);
        (string apId, _) = await registry.ResolveAsync(
            [new Fingerprint(FingerprintType.GoogleWifiDeviceId, apGwId)],
            source: "google-wifi",
            managementStatus: "discovered",
            ct: CancellationToken.None
        );

        // The AP owns its br-lan interface (keyed by its Guid device id), obscured MAC.
        await InsertInterfaceObscuredRowAsync(apId, "br-lan", "192.168.1.1", "703acb70d06*");

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The interface's real MAC was reconstructed…
        Assert.Equal(
            "70:3A:CB:70:D0:73",
            await ReadScalarAsync(
                $"SELECT mac_address FROM proj_interfaces WHERE device = '{apId}' AND interface = 'br-lan'"
            )
        );

        // …and the AP identity and the scanner's real-MAC device are now ONE device.
        string? viaGwId = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'google-wifi-device-id' AND fp_value = '{apGwId}'"
        );
        string? viaMac = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '{realMac}'"
        );
        Assert.NotNull(viaMac);
        Assert.Equal(viaGwId, viaMac);
    }

    [Fact]
    public async Task MaterializeAsync_InterfaceObscuredMac_AlreadyReconstructed_HealsSplitOnLaterPass()
    {
        // Reproduces the production split: a PRIOR pass already reconstructed the AP's
        // br-lan MAC (mac_address is set) but did NOT merge — the scanner's real-MAC
        // device was created separately and never unified. The old query dropped the
        // interface once mac_address was set, so the merge was never retried and the AP
        // stayed two devices forever. The reconstruction is no longer one-shot, so a
        // later materialization must re-resolve and HEAL the split — purely from the
        // stored mac_address, with no ARP/known-MAC row present to re-reconstruct from.
        const string apGwId = "abcdef0123456789";
        const string realMac = "703acb70d073";

        DeviceRegistry registry = new(_fixture.DataSource);

        // The AP device, keyed by its google-wifi-device-id.
        (string apId, _) = await registry.ResolveAsync(
            [new Fingerprint(FingerprintType.GoogleWifiDeviceId, apGwId)],
            source: "google-wifi",
            managementStatus: "discovered",
            ct: CancellationToken.None
        );

        // A separate scanner device already holds the AP's real MAC — the split state.
        (string scannerId, _) = await registry.ResolveAsync(
            [new Fingerprint(FingerprintType.Mac, realMac)],
            source: "scanner",
            managementStatus: "discovered",
            ct: CancellationToken.None
        );
        Assert.NotEqual(apId, scannerId); // genuinely two separate devices at the start

        // The AP's br-lan interface, with the obscured MAC AND mac_address already filled
        // by a previous reconstruction pass (colon form, as SetInterfaceMac writes it).
        await InsertInterfaceReconstructedRowAsync(
            apId,
            "br-lan",
            "192.168.1.1",
            "703acb70d06*",
            "70:3A:CB:70:D0:73"
        );

        await _materializer.MaterializeAsync(CancellationToken.None);

        // The AP identity and the scanner's real-MAC device are now ONE device.
        string? viaGwId = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'google-wifi-device-id' AND fp_value = '{apGwId}'"
        );
        string? viaMac = await ReadScalarAsync(
            $"SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'mac' AND fp_value = '{realMac}'"
        );
        Assert.NotNull(viaMac);
        Assert.Equal(viaGwId, viaMac);
    }

    // ── Promotion gaps (re-promote after first mint) ──────────────────────────

    [Fact]
    public async Task MaterializeAsync_VendorArrivesAfterFirstMint_PromotesOnLaterPass()
    {
        // A plain ARP sighting mints the device with a bare MAC fingerprint — no vendor/model.
        // GetNewDiscoveredMacs.sql's first-mint anti-join means the device is now permanently
        // excluded from that promotion path. A scanner identifying it days later must still
        // reach proj_hardware via the promotion-gap pass, not be silently dropped.
        await InsertArpRowAsync("observer-device-1", "10.0.0.60", "001122ffee01", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? vendorBefore = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM device_fingerprints f
                LEFT JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee01'
            """
        );
        Assert.Null(vendorBefore);

        // A scanner now identifies the same MAC.
        await InsertDiscoveredRowAsync(
            observer: "observer-device-2",
            ip: "10.0.0.60",
            mac: "001122ffee01",
            hostname: null,
            vendor: "Acme",
            model: "IPCam900"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? vendorAfter = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM device_fingerprints f
                JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee01'
            """
        );
        Assert.Equal("Acme", vendorAfter);
    }

    [Fact]
    public async Task MaterializeAsync_VendorArrivesAfterFirstMint_SerialOnlyDevice_PromotesOnLaterPass()
    {
        // Same first-mint gap as the MAC case, but for a device identified purely by UUID (no
        // MAC at all — e.g. a device behind a firewall a MAC-based observer can't see).
        // GetPromotionGapRows.sql must match on ssdp_uuid/onvif_serial/roku_serial/wsd_uuid too,
        // not just mac, or these devices never get re-evaluated.
        await InsertDiscoveredUuidRowAsync(
            "observer-device-1",
            "192.168.1.71",
            "660e8400-e29b-41d4-a716-446655440001",
            hostname: null,
            vendor: null,
            model: null
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? viaUuid = await ReadScalarAsync(
            "SELECT device_id::text FROM device_fingerprints WHERE fp_type = 'uuid' AND fp_value = '660e8400-e29b-41d4-a716-446655440001'"
        );
        Assert.NotNull(viaUuid);
        Assert.Null(await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{viaUuid}'"));

        // A scanner later re-observes the same device with vendor/model now present.
        await InsertDiscoveredUuidRowAsync(
            "observer-device-2",
            "192.168.1.72",
            "660e8400-e29b-41d4-a716-446655440001",
            hostname: null,
            vendor: "Acme",
            model: "Scanner9000"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(
            "Acme",
            await ReadScalarAsync($"SELECT system_vendor FROM proj_hardware WHERE device = '{viaUuid}'")
        );
        Assert.Equal(
            "Scanner9000",
            await ReadScalarAsync($"SELECT system_model FROM proj_hardware WHERE device = '{viaUuid}'")
        );
    }

    [Fact]
    public async Task MaterializeAsync_PromotionGap_DhcpHostnameArrivesAfterArpMint_Promotes()
    {
        // A device minted by a bare ARP sighting (no hostname) later gets a DHCP lease with a
        // hostname. DHCP promotion is first-mint-gated too (GetNewDhcpMacs.sql), and DHCP
        // hostnames live in proj_dhcp_leases, not proj_discovered — the promotion-gap pass must
        // check both.
        await InsertArpRowAsync("observer-device-1", "10.0.0.65", "001122ffee03", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None);

        Assert.Null(
            await ReadScalarAsync(
                """
                SELECT s.hostname FROM device_fingerprints f
                    LEFT JOIN proj_systems s ON s.device = f.device_id::text
                WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee03'
                """
            )
        );

        await ExecuteAsync(
            "INSERT INTO proj_dhcp_leases (service, scope, lease, hostname, updated_at) "
          + "VALUES ('dhcp-1', 'default', '001122ffee03', 'printer-lobby', now())"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? hostname = await ReadScalarAsync(
            """
            SELECT s.hostname FROM device_fingerprints f
                JOIN proj_systems s ON s.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee03'
            """
        );
        Assert.Equal("printer-lobby", hostname);
    }

    [Fact]
    public async Task MaterializeAsync_PromotionGap_NeverOverwritesExistingValue()
    {
        // A device already has a self-reported vendor. A later, conflicting proj_discovered
        // sighting for the same MAC must never clobber it — COALESCE keeps the original.
        await InsertDiscoveredRowAsync(
            observer: "observer-device-1",
            ip: "10.0.0.61",
            mac: "001122ffee02",
            hostname: null,
            vendor: "RealVendor"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        await InsertDiscoveredRowAsync(
            observer: "observer-device-2",
            ip: "10.0.0.62",
            mac: "001122ffee02",
            hostname: null,
            vendor: "ConflictingVendor"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? vendor = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM device_fingerprints f
                JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee02'
            """
        );
        Assert.Equal("RealVendor", vendor);
    }

    [Fact]
    public async Task MaterializeAsync_PromotionGap_FieldsSplitAcrossTwoDiscoveredRows_BothPromote()
    {
        // Regression guard for the GetPromotionGapRows.sql rewrite (four independent
        // correlated subqueries → one array_agg-per-column pass): vendor and model each need
        // "the most recent proj_discovered row where THIS field specifically is non-null", not
        // "values from a single most-recent row" — a naive single-row pick would return vendor
        // from the newest row even though it's null there, losing the value the older row had.
        await InsertArpRowAsync("observer-device-1", "10.0.0.80", "001122ffee04", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None);

        // Row 1: vendor only. Row 2 (different observer/IP, same MAC): model only.
        await InsertDiscoveredRowAsync(
            observer: "observer-device-1",
            ip: "10.0.0.80",
            mac: "001122ffee04",
            hostname: null,
            vendor: "Acme",
            model: null
        );
        await InsertDiscoveredRowAsync(
            observer: "observer-device-2",
            ip: "10.0.0.81",
            mac: "001122ffee04",
            hostname: null,
            vendor: null,
            model: "Scanner9000"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? vendor = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM device_fingerprints f
                JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee04'
            """
        );
        string? model = await ReadScalarAsync(
            """
            SELECT h.system_model
            FROM device_fingerprints f
                JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee04'
            """
        );
        Assert.Equal("Acme", vendor);
        Assert.Equal("Scanner9000", model);
    }

    [Fact]
    public async Task MaterializeAsync_PromotionGap_ObscuredMacRow_NeverPromotesVendor()
    {
        // Same contamination bug class already guarded in GetDeviceAllFacts.sql /
        // GetDeviceSightings.sql / DeviceListApi.cs / GetDeviceSummary.sql: a Google Wifi/OnHub row's
        // `mac` can be a RECONSTRUCTED value that happens to equal this device's real MAC
        // without being the same physical device. The promotion-gap pass must not adopt that
        // row's vendor.
        await InsertArpRowAsync("observer-device-1", "10.0.0.82", "001122ffee05", "eth0");
        await _materializer.MaterializeAsync(CancellationToken.None);

        await ExecuteAsync(
            "INSERT INTO proj_discovered (device, discovered, mac, obscured_mac, vendor, sources, updated_at) "
          + "VALUES ('google-wifi-ap', '192.168.1.220', '001122ffee05', '001122ffee0*', 'WrongVendor', "
          + "'google-wifi', now())"
        );
        await _materializer.MaterializeAsync(CancellationToken.None);

        string? vendor = await ReadScalarAsync(
            """
            SELECT h.system_vendor
            FROM device_fingerprints f
                JOIN proj_hardware h ON h.device = f.device_id::text
            WHERE f.fp_type = 'mac' AND f.fp_value = '001122ffee05'
            """
        );
        Assert.Null(vendor);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task InsertInterfaceReconstructedRowAsync(
        string device,
        string iface,
        string ipv4,
        string obscuredMac,
        string macAddress
    )
    {
        const string sql = """
            INSERT INTO proj_interfaces (device, interface, ipv4, obscured_mac, mac_address)
            VALUES (@device, @iface, @ipv4, @obscured, @mac)
            ON CONFLICT (device, interface) DO UPDATE
              SET ipv4 = EXCLUDED.ipv4, obscured_mac = EXCLUDED.obscured_mac,
                  mac_address = EXCLUDED.mac_address
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("iface", iface);
        cmd.Parameters.AddWithValue("ipv4", ipv4);
        cmd.Parameters.AddWithValue("obscured", obscuredMac);
        cmd.Parameters.AddWithValue("mac", macAddress);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertInterfaceObscuredRowAsync(
        string device,
        string iface,
        string ipv4,
        string obscuredMac
    )
    {
        const string sql = """
            INSERT INTO proj_interfaces (device, interface, ipv4, obscured_mac)
            VALUES (@device, @iface, @ipv4, @obscured)
            ON CONFLICT (device, interface) DO UPDATE
              SET ipv4 = EXCLUDED.ipv4, obscured_mac = EXCLUDED.obscured_mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("iface", iface);
        cmd.Parameters.AddWithValue("ipv4", ipv4);
        cmd.Parameters.AddWithValue("obscured", obscuredMac);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertArpRowAsync(
        string device,
        string ip,
        string mac,
        string iface
    )
    {
        const string sql = """
            INSERT INTO proj_device_arp (device, arp, mac, iface, state)
            VALUES (@device, @ip, @mac, @iface, 'reachable')
            ON CONFLICT (device, arp) DO UPDATE SET mac = EXCLUDED.mac
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", mac);
        cmd.Parameters.AddWithValue("iface", iface);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveredRowAsync(
        string observer,
        string ip,
        string? mac,
        string? hostname,
        string? vendor = null,
        string? model = null,
        string? os = null
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, mac, hostname, vendor, model, os)
            VALUES (@device, @ip, @mac, @hostname, @vendor, @model, @os)
            ON CONFLICT (device, discovered) DO UPDATE
              SET mac = EXCLUDED.mac, hostname = EXCLUDED.hostname, os = EXCLUDED.os
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", (object?)mac ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("os", (object?)os ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSshDiscoveredRowAsync(
        string observer,
        string ip,
        string? mac,
        string sshHostKey
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, mac, ssh_host_key)
            VALUES (@device, @ip, @mac, @key)
            ON CONFLICT (device, discovered) DO UPDATE
              SET mac = EXCLUDED.mac, ssh_host_key = EXCLUDED.ssh_host_key
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("mac", (object?)mac ?? DBNull.Value);
        cmd.Parameters.AddWithValue("key", sshHostKey);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertObscuredDiscoveredRowAsync(
        string observer,
        string ip,
        string obscuredMac,
        string? hostname
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, obscured_mac, hostname)
            VALUES (@device, @ip, @obscured, @hostname)
            ON CONFLICT (device, discovered) DO UPDATE
              SET obscured_mac = EXCLUDED.obscured_mac, hostname = EXCLUDED.hostname
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("obscured", obscuredMac);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCastDiscoveredNoObscuredRowAsync(
        string observer,
        string ip,
        string castId,
        string? friendlyName,
        string? deviceType
    )
    {
        // A networkState-only cast row: carries the cast id + intrinsics but NO
        // obscured_mac, so it never appears in GetObscuredMacRows.
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, cast_id, friendly_name, device_type)
            VALUES (@device, @ip, @cast, @friendly, @dtype)
            ON CONFLICT (device, discovered) DO UPDATE
              SET cast_id = EXCLUDED.cast_id, friendly_name = EXCLUDED.friendly_name,
                  device_type = EXCLUDED.device_type
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("cast", castId);
        cmd.Parameters.AddWithValue("friendly", (object?)friendlyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dtype", (object?)deviceType ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        // GetCastIdIpCounts now reads materialization_facts (docs/plans/architecture-identity-facts.md
        // §5 Phase 2a) — these direct-SQL seeds bypass the router's dual write, so mirror it here.
        await InsertMaterializationFactAsync(observer, ip, "Device[].Discovered[].CastId", castId);
    }

    private async Task InsertCastDiscoveredRowAsync(
        string observer,
        string ip,
        string obscuredMac,
        string castId,
        string? friendlyName,
        string? deviceType
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, obscured_mac, cast_id, friendly_name, device_type)
            VALUES (@device, @ip, @obscured, @cast, @friendly, @dtype)
            ON CONFLICT (device, discovered) DO UPDATE
              SET obscured_mac = EXCLUDED.obscured_mac, cast_id = EXCLUDED.cast_id,
                  friendly_name = EXCLUDED.friendly_name, device_type = EXCLUDED.device_type
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("obscured", obscuredMac);
        cmd.Parameters.AddWithValue("cast", castId);
        cmd.Parameters.AddWithValue("friendly", (object?)friendlyName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dtype", (object?)deviceType ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        // See InsertCastDiscoveredNoObscuredRowAsync — mirrors the router's dual write.
        await InsertMaterializationFactAsync(observer, ip, "Device[].Discovered[].CastId", castId);
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

    private async Task InsertDhcpLeaseAsync(
        string service,
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
        cmd.Parameters.AddWithValue("scope", "default");
        cmd.Parameters.AddWithValue("lease", mac);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDhcpLocalLeaseAsync(string device, string mac, string ip, string? hostname)
    {
        const string sql = """
            INSERT INTO proj_dhcp_local_leases (device, lease, ip, hostname)
            VALUES (@device, @lease, @ip, @hostname)
            ON CONFLICT (device, lease) DO NOTHING
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", device);
        cmd.Parameters.AddWithValue("lease", mac);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveredUuidRowAsync(
        string observer,
        string ip,
        string ssdpUuid,
        string? hostname,
        string? vendor,
        string? model
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, ssdp_uuid, hostname, vendor, model)
            VALUES (@device, @ip, @uuid, @hostname, @vendor, @model)
            ON CONFLICT (device, discovered) DO UPDATE
              SET ssdp_uuid = EXCLUDED.ssdp_uuid, hostname = EXCLUDED.hostname,
                  vendor = EXCLUDED.vendor, model = EXCLUDED.model
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("uuid", ssdpUuid);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveredSnmpSerialRowAsync(
        string observer,
        string ip,
        string snmpSerial,
        string? hostname,
        string? vendor,
        string? model
    )
    {
        const string sql = """
            INSERT INTO proj_discovered (device, discovered, snmp_serial, hostname, vendor, model)
            VALUES (@device, @ip, @serial, @hostname, @vendor, @model)
            ON CONFLICT (device, discovered) DO UPDATE
              SET snmp_serial = EXCLUDED.snmp_serial, hostname = EXCLUDED.hostname,
                  vendor = EXCLUDED.vendor, model = EXCLUDED.model
            """;
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.AddWithValue("device", observer);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("serial", snmpSerial);
        cmd.Parameters.AddWithValue("hostname", (object?)hostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vendor", (object?)vendor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadScalarAsync(string sql)
    {
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : result.ToString();
    }
}