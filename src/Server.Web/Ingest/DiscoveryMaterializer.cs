using JMW.Discovery.Core;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server;

/// <summary>
/// Post-ingest pass that creates 'discovered' device records for network nodes
/// observed in passive projection tables (ARP cache, DHCP leases, scanner results)
/// that have not yet been formally registered as devices.
/// After registration, all available metadata from the source projection row is
/// bootstrapped into the new device's own projection tables (proj_systems,
/// proj_hardware) using COALESCE so authoritative agent-supplied values are never
/// overwritten.
/// Called by FactIngestPipeline after each batch commits, with all DB readers
/// fully closed. Never opens a second connection while iterating a reader.
/// </summary>
public sealed class DiscoveryMaterializer
{
    /// <summary>
    /// Projection tables every sub-pass above ultimately reads (directly or via a query it calls).
    /// FactsEndpoint only calls <see cref="MaterializeAsync" /> when a batch's routed facts touched
    /// one of these (performance-03) — a batch that never wrote any of them (e.g. a services-only
    /// POST, or a discovery-cadence tick that produced no ARP/interface rows) can't have changed
    /// what any pass here would find, so running the full 8-pass sweep just to see nothing new is
    /// pure waste. Keep this in sync with the tables each MaterializeXAsync/GetXRows(.sql) reads —
    /// it is intentionally broad (a superset is always safe; a missed table means a stale run of
    /// the pass it feeds, not a crash).
    /// Home Assistant device promotion is NOT here — it resolves inline in FactsEndpoint off the
    /// batch's own in-memory facts instead of a projection reread; see
    /// docs/plans/ha-inline-discovery.md.
    /// </summary>
    public static readonly IReadOnlySet<string> RelevantTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "proj_device_arp", // MaterializeArpMacsAsync
        "proj_dhcp_leases", // MaterializeDiscoveredSourceAsync(DhcpMacs); GetPromotionGapRows hostname fallback
        "proj_dhcp_local_leases", // MaterializeDiscoveredSourceAsync(DhcpLocalMacs); GetPromotionGapRows fallback
        "proj_discovered", // ScannerMacs/ScannerSerials/obscured-MAC/SSH-host-key/scanner-id sources
        "materialization_facts", // dual-written identity signals (docs/plans/architecture-identity-facts.md)
        "proj_interfaces", // MaterializeInterfaceMacsAsync (Google Wifi AP obscured interface MACs)
        "proj_hardware", // MaterializePromotionGapsAsync gap detection + promotion target
        "proj_systems", // MaterializePromotionGapsAsync gap detection + promotion target
    };

    /// <summary>Whether an identity input is read from a projection value column or a dimension key.</summary>
    public enum IdentityInputKind
    {
        /// <summary>A projection value column (maps to a FactPaths const via ProjectionLibrary).</summary>
        Value,

        /// <summary>A projection dimension key (maps to a DimKey; no const can express it).</summary>
        DimensionKey,
    }

    /// <summary>One projection read this materializer performs as a device-identity input.</summary>
    public readonly record struct IdentityInputColumn(string Table, string ColumnOrDimension, IdentityInputKind Kind);

    /// <summary>
    /// The column-grain companion to <see cref="RelevantTables" />: every projection value column or
    /// dimension key a pass in this file reads as a device fingerprint or promotion input (NFR-8,
    /// architecture §4.3). This is the authoritative read-set the operator-facts identity exclusion
    /// is checked against — the two-arm exact set-equality fitness test maps each <c>Value</c> entry
    /// to a <c>FactPaths</c> const (asserting it is in <c>OperatorFactCatalog.IdentityBearingFactPaths</c>)
    /// and each <c>DimensionKey</c> entry to a <c>DimKey</c> (asserting it is in
    /// <c>OperatorFactCatalog.IdentityBearingDimensions</c>). <b>Code-review rule:</b> adding a
    /// projection read to any pass here means adding its (table, column|dimension) entry below, or the
    /// fitness test fails. Hand-maintained (full automation would require parsing the <c>.sql</c>
    /// SELECT lists); the exact-equality test forces this list and the exclusion set to move together.
    /// </summary>
    public static readonly IReadOnlyList<IdentityInputColumn> IdentityInputColumns =
    [
        // Tier 1 — identity/merge-critical value columns.
        new("proj_device_arp", "mac", IdentityInputKind.Value), // GetNewArpMacs, GetKnownMacsForIp
        new("proj_discovered", "mac", IdentityInputKind.Value), // GetNewDiscoveredMacs, GetKnownMacsForIp
        new("proj_discovered", "obscured_mac", IdentityInputKind.Value), // GetObscuredMacRows
        new("proj_discovered", "onvif_serial", IdentityInputKind.Value), // GetNewDiscoveredSerials, GetPromotionGapRows
        new("proj_discovered", "roku_serial", IdentityInputKind.Value), // GetNewDiscoveredSerials, GetPromotionGapRows
        new("proj_discovered", "snmp_serial", IdentityInputKind.Value), // GetNewDiscoveredSerials, GetPromotionGapRows
        new("proj_discovered", "ssdp_uuid", IdentityInputKind.Value), // GetNewDiscoveredSerials, GetPromotionGapRows
        new("proj_discovered", "wsd_uuid", IdentityInputKind.Value), // GetNewDiscoveredSerials, GetPromotionGapRows
        new("proj_discovered", "ssh_host_key", IdentityInputKind.Value), // GetSshHostKeyRows
        new("proj_discovered", "hue_bridge_id", IdentityInputKind.Value), // GetScannerIdRows
        new("proj_discovered", "onvif_hardware_id", IdentityInputKind.Value), // GetScannerIdRows
        new("proj_discovered", "cast_id", IdentityInputKind.Value), // GetObscuredMacRows (GetCastIdIpCounts moved to materialization_facts, Phase 2a)
        new("proj_interfaces", "mac_address", IdentityInputKind.Value), // GetInterfaceObscuredMacRows
        new("proj_interfaces", "obscured_mac", IdentityInputKind.Value), // GetInterfaceObscuredMacRows
        new("proj_interfaces", "ipv4", IdentityInputKind.Value), // GetInterfaceObscuredMacRows (join key)
        new("proj_dhcp_local_leases", "ip", IdentityInputKind.Value), // GetNewDhcpLocalMacs, GetKnownMacsForIp

        // Tier 2 — promotion input value columns.
        new("proj_discovered", "hostname", IdentityInputKind.Value), // GetPromotionGapRows, discovered promote
        new("proj_discovered", "friendly_name", IdentityInputKind.Value), // GetObscuredMacRows, GetPromotionGapRows
        new("proj_discovered", "vendor", IdentityInputKind.Value), // discovered promote, GetPromotionGapRows, GetObscuredMacRows
        new("proj_discovered", "model", IdentityInputKind.Value), // discovered promote, GetPromotionGapRows
        new("proj_discovered", "os", IdentityInputKind.Value), // discovered promote, GetPromotionGapRows, GetObscuredMacRows
        new("proj_discovered", "device_type", IdentityInputKind.Value), // GetObscuredMacRows
        new("proj_hardware", "system_vendor", IdentityInputKind.Value), // GetPromotionGapRows gap detection
        new("proj_hardware", "system_model", IdentityInputKind.Value), // GetPromotionGapRows gap detection
        new("proj_systems", "hostname", IdentityInputKind.Value), // GetPromotionGapRows gap detection
        new("proj_systems", "os_family", IdentityInputKind.Value), // GetPromotionGapRows gap detection
        new("proj_systems", "friendly_name", IdentityInputKind.Value), // GetPromotionGapRows — gap-fill-only; operator-authorable via OperatorFactCatalog.GapFillOnlyFactPaths
        new("proj_dhcp_local_leases", "hostname", IdentityInputKind.Value), // GetPromotionGapRows fallback

        // Dimension keys that are themselves fingerprints.
        new("proj_dhcp_local_leases", "Lease", IdentityInputKind.DimensionKey), // key is a MAC — GetNewDhcpLocalMacs, GetKnownMacsForIp
    ];

    private readonly ILogger<DiscoveryMaterializer> _logger;
    private readonly NpgsqlDataSource _db;

    public DiscoveryMaterializer(NpgsqlDataSource db, ILogger<DiscoveryMaterializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MaterializeAsync(CancellationToken ct)
    {
        // One connection shared across all passes — 5 pool acquisitions → 1.
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await MaterializeArpMacsAsync(conn, ct);
        await MaterializeDiscoveredSourceAsync(conn, DhcpMacs, ct);
        await MaterializeDiscoveredSourceAsync(conn, DhcpLocalMacs, ct);
        // Fill in reconstructed MACs before the discovered pass consumes them; ARP/DHCP
        // above have already populated the known-MAC set this pass matches against.
        await MaterializeObscuredMacsAsync(conn, ct);
        await MaterializeInterfaceMacsAsync(conn, ct);
        await MaterializeDiscoveredSourceAsync(conn, ScannerMacs, ct);
        await MaterializeDiscoveredSourceAsync(conn, ScannerSerials, ct);
        // After the MAC passes, so a host-key merges onto any device already resolved
        // by its MAC (and unifies observations that share the key across observers/IPs).
        await MaterializeSshHostKeysAsync(conn, ct);
        await MaterializeScannerIdsAsync(conn, ct);
        // Last: MaterializeDiscoveredSourceAsync above only promotes intrinsics for a MAC's
        // FIRST-ever fingerprinting pass (GetNewDiscoveredMacs.sql's anti-join). A device
        // fingerprinted early by plain ARP/DHCP (no vendor/model/os) never revisits that
        // promotion once minted — this pass closes that gap on every cycle instead.
        await MaterializePromotionGapsAsync(conn, ct);
    }

    private static async Task MaterializePromotionGapsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        List<(string? Device, string? Vendor, string? Model, string? Os, string? Hostname, string? FriendlyName)>
            rows = await conn.GetPromotionGapRowsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for the upserts below.

        foreach ((string? device, string? vendor, string? model, string? os, string? hostname, string? friendlyName)
            in rows)
        {
            if (device is null)
            {
                continue;
            }

            // UpsertDeviceHardware/UpsertDeviceSystem/UpsertDeviceSummary COALESCE against the
            // existing column, so calling them with a still-null value is a safe no-op — but skip
            // entirely when there's nothing to offer, so a permanently-unfillable gap doesn't
            // churn updated_at.
            if (vendor is not null || model is not null)
            {
                await conn.UpsertDeviceHardwareAsync(device, vendor, model, serial: null, ct).ExecuteAsync(ct);
            }

            // Also reaches the unified proj_devices.vendor (DeviceVendorDerivation covers the
            // agent-direct path; this covers passive-discovery promotion for agentless devices).
            if (vendor is not null)
            {
                await conn.UpsertDeviceSummaryAsync(device, vendor, kind: null, ct).ExecuteAsync(ct);
            }

            if (os is not null || hostname is not null || friendlyName is not null)
            {
                await conn.UpsertDeviceSystemAsync(device, hostname, friendlyName, lastSeenIp: null, os, ct)
                    .ExecuteAsync(ct);
            }
        }
    }

    private async Task MaterializeScannerIdsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Vendor identity ids (Hue bridge id, ONVIF hardware id) parsed by the probes. Each is a
        // stable per-unit id → a ChassisSerial fingerprint, unioned with the row's MAC so the id
        // merges onto the device an ARP/scanner observer sees by MAC. An extra fingerprint is
        // cheap and may be the only match when an observer has the id but not the MAC.
        List<(string? Mac, string? HueBridgeId, string? OnvifHardwareId)> rows =
            await conn.GetScannerIdRowsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for the resolves below.

        foreach ((string? mac, string? hueBridgeId, string? onvifHardwareId) in rows)
        {
            List<Fingerprint> fps = [];
            if (!string.IsNullOrWhiteSpace(hueBridgeId))
            {
                fps.Add(new Fingerprint(FingerprintType.ChassisSerial, hueBridgeId));
            }

            if (!string.IsNullOrWhiteSpace(onvifHardwareId))
            {
                fps.Add(new Fingerprint(FingerprintType.ChassisSerial, onvifHardwareId));
            }

            if (fps.Count == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mac))
            {
                fps.Add(new Fingerprint(FingerprintType.Mac, mac));
            }

            await TryResolveDiscoveredAsync(conn, fps, source: "scanner", ct, logTag: "scanner-id");
        }
    }

    private async Task MaterializeSshHostKeysAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        List<(string? Mac, string? SshHostKey)> rows = await conn.GetSshHostKeyRowsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for the resolves below.

        foreach ((string? mac, string? sshHostKey) in rows)
        {
            if (string.IsNullOrEmpty(sshHostKey))
            {
                continue;
            }

            // The host key is a stable per-host identity; unioning it with the row's MAC
            // merges any device already resolved by that MAC, and cross-observer rows that
            // share the key converge onto one device. (Caveat: VMs cloned from one image
            // share a host key until it is regenerated — the standard identity-by-host-key
            // trade-off, consistent with the serial/uuid fingerprints.)
            List<Fingerprint> fps = [new(FingerprintType.SshHostKey, sshHostKey)];
            if (!string.IsNullOrEmpty(mac))
            {
                fps.Add(new Fingerprint(FingerprintType.Mac, mac));
            }

            await TryResolveDiscoveredAsync(conn, fps, source: "ssh-banner", ct);
        }
    }

    private async Task MaterializeArpMacsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        List<DiscoveredMacResult> macs = await conn.GetNewArpMacsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for writes below.

        foreach (DiscoveredMacResult result in macs)
        {
            if (string.IsNullOrEmpty(result.Mac))
            {
                continue;
            }

            await TryResolveDiscoveredAsync(
                conn,
                [new Fingerprint(FingerprintType.Mac, result.Mac)],
                source: "arp",
                ct
            );
        }
    }

    // ── Resolve wrapper (review D21) ──────────────────────────────────────────

    /// <summary>
    /// Resolves/merges a device from <paramref name="fingerprints" />, swallowing the
    /// "every fingerprint was unusable" case (all failed MAC normalization) rather than
    /// aborting the materialize pass — the observation is kept, just no device is minted this
    /// round. Returns the resolved device id, or null on that failure. Only wraps sites whose
    /// try block is the resolve call alone; a couple of call sites also run follow-up upserts
    /// inside the same try (so an upsert failure is deliberately treated the same as a resolve
    /// failure there) and keep their own try/catch rather than use this.
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="logTag">
    /// The tag logged on failure, when it differs from <paramref name="source" /> (e.g. the
    /// scanner-id source resolves as "scanner" but logs failures as "scanner-id"). Defaults to
    /// <paramref name="source" />.
    /// </param>
    /// <param name="conn"></param>
    /// <param name="fingerprints"></param>
    /// <param name="source"></param>
    private async Task<string?> TryResolveDiscoveredAsync(
        NpgsqlConnection conn,
        List<Fingerprint> fingerprints,
        string source,
        CancellationToken ct,
        string? logTag = null
    )
    {
        try
        {
            (string deviceId, bool _) = await DeviceRegistry.ResolveWithConnectionAsync(
                conn,
                fingerprints,
                source: source,
                managementStatus: "discovered",
                ct: ct
            );
            return deviceId;
        }
        catch (ArgumentException)
        {
            DiscoveryMaterializerLog.MacNormalizationFailed(_logger, logTag ?? source);
            return null;
        }
    }

    // ── Discovered-row sources (DHCP + scanner) ───────────────────────────────
    //
    // These sources all follow one shape: read the "new" rows (each query embeds its own
    // not-yet-a-fingerprint anti-join — the resolve-only-new contract), resolve/merge a
    // device from the row's fingerprints, then promote its intrinsics. They differ only in
    // source tag, an optional table guard, and which intrinsics they promote — so they are
    // data (a DiscoverySource) driving one loop, not four near-identical methods.

    private sealed record DiscoverySource(
        string Source,
        Func<NpgsqlConnection, CancellationToken, IAsyncEnumerable<DiscoveredRow>> Query,
        bool PromoteIp, // pass the row's IP to last_seen_ip (vs leave null)
        bool PromoteHardware, // upsert vendor/model/serial
        string? RequiresTable = null // skip if this projection table doesn't exist yet
    );

    private static readonly DiscoverySource DhcpMacs =
        new("dhcp", (c, ct) => c.GetNewDhcpMacsAsync(ct), PromoteIp: false, PromoteHardware: false);

    private static readonly DiscoverySource DhcpLocalMacs = new(
        "dhcp-local",
        (c, ct) => c.GetNewDhcpLocalMacsAsync(ct),
        PromoteIp: true,
        PromoteHardware: false,
        RequiresTable: "proj_dhcp_local_leases"
    );

    private static readonly DiscoverySource ScannerMacs =
        new("scanner", (c, ct) => c.GetNewDiscoveredMacsAsync(ct), PromoteIp: true, PromoteHardware: true);

    // Devices identified only by serial/uuid (no MAC in scanner data).
    private static readonly DiscoverySource ScannerSerials =
        new("scanner", (c, ct) => c.GetNewDiscoveredSerialsAsync(ct), PromoteIp: true, PromoteHardware: true);

    private async Task MaterializeDiscoveredSourceAsync(
        NpgsqlConnection conn,
        DiscoverySource src,
        CancellationToken ct
    )
    {
        if (src.RequiresTable is { } table)
        {
            TableExistsResult check = await conn.ProjectionTableExistsAsync(table, ct).FirstOrDefaultAsync(ct);
            if (check.Exists != true)
            {
                return;
            }
        }

        List<DiscoveredRow> rows = (await src.Query(conn, ct).ToListAsync(ct))
            .Select(r => r.Clean())
            .Where(HasAnyFingerprint)
            .ToList();
        // Reader closed — conn is free for the resolves + promotes below.

        List<(string DeviceId, DiscoveredRow Row)> resolved = new(rows.Count);
        foreach (DiscoveredRow row in rows)
        {
            string? deviceId = await TryResolveDiscoveredAsync(conn, BuildFingerprints(row), source: src.Source, ct);
            if (deviceId is not null)
            {
                resolved.Add((deviceId, row));
            }
        }

        foreach ((string deviceId, DiscoveredRow row) in resolved)
        {
            string? ip = src.PromoteIp ? row.Ip : null;
            if (row.Hostname is not null || ip is not null || row.Os is not null)
            {
                // No friendly-name-ish source available on this row (proj_discovered.friendly_name
                // isn't selected here) — MaterializePromotionGapsAsync fills it in on a later pass.
                await conn.UpsertDeviceSystemAsync(deviceId, row.Hostname, friendlyName: null, ip, row.Os, ct)
                    .ExecuteAsync(ct);
            }

            if (src.PromoteHardware)
            {
                string? serial = row.OnvifSerial ?? row.RokuSerial ?? row.SnmpSerial;
                if (row.Vendor is not null || row.Model is not null || serial is not null)
                {
                    await conn.UpsertDeviceHardwareAsync(deviceId, row.Vendor, row.Model, serial, ct).ExecuteAsync(ct);
                }

                // Also reaches the unified proj_devices.vendor (see DeviceVendorDerivation).
                if (row.Vendor is not null)
                {
                    await conn.UpsertDeviceSummaryAsync(deviceId, row.Vendor, kind: null, ct).ExecuteAsync(ct);
                }
            }
        }
    }

    // ── Fingerprint assembly ──────────────────────────────────────────────────

    private static List<Fingerprint> BuildFingerprints(DiscoveredRow row)
    {
        List<Fingerprint> fps = new(5);

        if (!string.IsNullOrEmpty(row.Mac))
        {
            fps.Add(new Fingerprint(FingerprintType.Mac, row.Mac));
        }

        if (!string.IsNullOrEmpty(row.OnvifSerial))
        {
            fps.Add(new Fingerprint(FingerprintType.ChassisSerial, row.OnvifSerial));
        }

        if (!string.IsNullOrEmpty(row.RokuSerial))
        {
            fps.Add(new Fingerprint(FingerprintType.ChassisSerial, row.RokuSerial));
        }

        if (!string.IsNullOrEmpty(row.SnmpSerial))
        {
            fps.Add(new Fingerprint(FingerprintType.ChassisSerial, row.SnmpSerial));
        }

        if (!string.IsNullOrEmpty(row.SsdpUuid))
        {
            fps.Add(new Fingerprint(FingerprintType.Uuid, row.SsdpUuid));
        }

        if (!string.IsNullOrEmpty(row.WsdUuid))
        {
            fps.Add(new Fingerprint(FingerprintType.Uuid, row.WsdUuid));
        }

        return fps;
    }

    // ── Obscured-MAC reconstruction (Google Wifi) ────────────────────────────

    /// <summary>
    /// Given an OUI extracted from an obscured MAC, looks up every real MAC known to have been
    /// seen at <paramref name="ip" /> and returns the one matching that OUI, or null if none
    /// matches. Byte-identical lookup shared by the two obscured-MAC reconstruction passes
    /// (per-device and per-interface) — the guard condition, persistence call, and logging on
    /// success/failure differ per pass and stay at each call site.
    /// </summary>
    private static async Task<string?> TryReconstructMacAsync(
        NpgsqlConnection conn,
        string ip,
        string oui,
        CancellationToken ct
    )
    {
        List<string?> ipMacs = (await conn.GetKnownMacsForIpAsync(ip, ct).ToListAsync(ct))
            .Select(r => r.Mac)
            .ToList();
        return ObscuredMac.Pick(ipMacs, oui);
    }

    private async Task MaterializeObscuredMacsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Google Wifi obscures MACs (real OUI, obfuscated device bytes, trailing '*'),
        // kept as the raw obscured_mac fact. Identity is anchored to the stable Cast
        // device id when present (FingerprintType.CastId), decoupled from IP/MAC, so a
        // stale mDNS advertisement on a reused IP can't smear the cast device's name
        // onto the new occupant. Per cycle:
        //   • pre-compute distinct-IP count per cast id (full proj_discovered);
        //   1. reconstruct the full MAC (by IP + OUI) for rows not yet resolved;
        //   2. resolve the device — cast id alone when it spans >1 IP (stale/roam),
        //      else cast id + MAC — and PROMOTE intrinsics.
        // Promotion runs every cycle so late-arriving enrichment still graduates;
        // COALESCE upserts make re-promotion a no-op.
        List<(string Device, string Ip, string? ObscuredMac, string? Mac, string? Hostname, string? Model, string?
            FriendlyName, string? DeviceType, string? CastId, string? Vendor, string? Os)> rows =
            await conn.GetObscuredMacRowsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for the per-row lookups + writes below.

        // Distinct-IP count per cast id, over the FULL proj_discovered table (reader
        // materialized up front, before the Phase-1 writes). Must be the full table,
        // not the obscured-MAC subset above: a cast device's *current* row often
        // carries the cast id with no obscured_mac (networkState-only), so counting
        // only obscured rows would undercount its IPs and wrongly co-register.
        Dictionary<string, int> castIdIpCount =
            (await conn.GetCastIdIpCountsAsync(ct).ToListAsync(ct))
            .ToDictionary(r => r.CastId, r => (int)(r.IpCount ?? 0), StringComparer.Ordinal);

        // ── Phase 1: reconstruct the full MAC for each row (best-effort). ──
        List<ObscuredRow> resolved = new(rows.Count);
        foreach ((string device, string ip, string? obscured, string? existingMac, string? hostname, string? model,
            string? friendlyName, string? deviceType, string? castId, string? vendor, string? os) in rows)
        {
            string? full = NullIfBlank(existingMac);
            if (full is null && obscured is not null && ObscuredMac.TryGetOui(obscured, out string oui))
            {
                full = await TryReconstructMacAsync(conn, ip, oui, ct);
                if (full is not null)
                {
                    await conn.SetDiscoveredMacAsync(device, ip, full, ct).ExecuteAsync(ct);
                    DiscoveryMaterializerLog.MacReconstructed(_logger, obscured, full);
                }
                else
                {
                    DiscoveryMaterializerLog.MacUnresolved(_logger, obscured);
                }
            }

            resolved.Add(
                new ObscuredRow(
                    ip,
                    NullIfBlank(obscured),
                    full,
                    hostname,
                    model,
                    friendlyName,
                    deviceType,
                    NullIfBlank(castId),
                    vendor,
                    os
                )
            );
        }

        // ── Phase 2: resolve identity and promote intrinsics. ──
        foreach (ObscuredRow r in resolved)
        {
            // Hardware identity of the thing at this IP: the obscured MAC (always
            // present for these rows) plus the real MAC when reconstructed. The
            // obscured MAC keeps all of this device's OnHub data cohesive even when
            // the real MAC can't be reconstructed; the real MAC bridges to other
            // observers (ARP/scanner) when it can.
            List<Fingerprint> hardwareFps = [];
            if (r.ObscuredMac is { } obscured)
            {
                hardwareFps.Add(new Fingerprint(FingerprintType.ObscuredMac, obscured));
            }

            if (r.Mac is { } mac)
            {
                hardwareFps.Add(new Fingerprint(FingerprintType.Mac, mac));
            }

            if (r.CastId is { } castId && castIdIpCount.GetValueOrDefault(castId, 0) >= 2)
            {
                // The cast id is advertised at >1 IP. A Cast device has a single NIC,
                // so the IPs are different physical devices (e.g. a wired box grouped
                // with — or reflecting the mDNS of — a wireless speaker). The name must
                // never smear onto hardware an independent observer sees as its own.
                if (r.Mac is not null)
                {
                    // This IP resolved to a real MAC a scanner/ARP observer also sees.
                    // Keep it a name-less device on its own MAC(s); the cast id/name is
                    // resolved separately so it can't land on this distinct hardware.
                    await ResolveAndPromoteAsync(conn, hardwareFps, promoteFrom: null, ct);
                    await ResolveAndPromoteAsync(conn, [new Fingerprint(FingerprintType.CastId, castId)], r, ct);
                }
                else
                {
                    // No real MAC here — the obscured MAC has no cross-observer reach,
                    // so unifying it with the cast identity can't drag in another
                    // device. This keeps the actual cast device (obscured MAC + cast id
                    // + name) as one record instead of fragmenting it.
                    List<Fingerprint> fps = [.. hardwareFps, new(FingerprintType.CastId, castId)];
                    await ResolveAndPromoteAsync(conn, fps, r, ct);
                }
            }
            else
            {
                // No cast id, or a single-IP cast id: one unified device carrying the
                // hardware MAC(s), the cast id (if any), and the mDNS intrinsics.
                List<Fingerprint> fps = [.. hardwareFps];
                if (r.CastId is { } single)
                {
                    fps.Add(new Fingerprint(FingerprintType.CastId, single));
                }

                await ResolveAndPromoteAsync(conn, fps, r, ct);
            }
        }
    }

    /// <summary>
    /// Resolves a device from the given fingerprints and, when <paramref name="promoteFrom" />
    /// is supplied, promotes its intrinsic mDNS attributes (hostname/friendly name,
    /// model, device-type→kind) onto it. A resolve whose fingerprints all normalize
    /// away (e.g. a lone locally-administered MAC) is caught so one row can't abort the pass.
    /// </summary>
    private async Task ResolveAndPromoteAsync(
        NpgsqlConnection conn,
        List<Fingerprint> fingerprints,
        ObscuredRow? promoteFrom,
        CancellationToken ct
    )
    {
        if (fingerprints.Count == 0)
        {
            return;
        }

        try
        {
            (string deviceId, bool _) = await DeviceRegistry.ResolveWithConnectionAsync(
                conn,
                fingerprints,
                source: "google-wifi",
                managementStatus: "discovered",
                ct: ct
            );

            if (promoteFrom is not { } r)
            {
                return;
            }

            // The sighting/link telemetry stays on the observation in proj_discovered.
            // Hostname and friendly name are genuinely distinct here — never conflated: the mDNS
            // hostname (if reported) is the real hostname; the mDNS/UPnP friendly name (e.g. "Kitchen
            // Audio") is display-only. OS rides along the same COALESCE upsert (the row's os column
            // is the station-level hint — e.g. VendorOsFromDiscoveredTypeDerivation's "linux" for an
            // OnHub mesh unit — never overwriting an agent-collected os_family).
            string? cleanHostname = NullIfBlank(r.Hostname);
            string? cleanFriendlyName = NullIfBlank(r.FriendlyName);
            string? cleanOs = NullIfBlank(r.Os);
            if (cleanHostname != null || cleanFriendlyName != null || cleanOs != null)
            {
                await conn.UpsertDeviceSystemAsync(
                        deviceId,
                        cleanHostname,
                        cleanFriendlyName,
                        NullIfBlank(r.Ip),
                        cleanOs,
                        ct
                    )
                    .ExecuteAsync(ct);
            }

            // Model → Hardware.SystemModel. Hardware vendor is intentionally not set: proj_hardware
            // vendor is reserved for the agent/dmidecode path.
            if (NullIfBlank(r.Model) is { } cleanModel)
            {
                await conn.UpsertDeviceHardwareAsync(deviceId, vendor: null, cleanModel, serial: null, ct)
                    .ExecuteAsync(ct);
            }

            // DeviceType → Kind (e.g. "Nest-Audio", "OnHub Mesh Point") and Vendor → proj_devices
            // vendor, matching the generic discovered-promote path. The row's vendor column is the
            // station-level UPnP manufacturer or a kind-derived vendor (OnHub → Google via
            // VendorOsFromDiscoveredTypeDerivation) — a device manufacturer, not an OUI guess.
            string? cleanVendor = NullIfBlank(r.Vendor);
            string? cleanKind = NullIfBlank(r.DeviceType);
            if (cleanVendor != null || cleanKind != null)
            {
                await conn.UpsertDeviceSummaryAsync(deviceId, cleanVendor, cleanKind, ct).ExecuteAsync(ct);
            }
        }
        catch (ArgumentException)
        {
            // Every fingerprint was unusable. Keep the observation; mint no device.
            DiscoveryMaterializerLog.MacNormalizationFailed(_logger, "google-wifi");
        }
    }

    /// <summary>A discovered obscured-MAC row after MAC reconstruction (phase 1).</summary>
    private sealed record ObscuredRow(
        string Ip,
        string? ObscuredMac,
        string? Mac,
        string? Hostname,
        string? Model,
        string? FriendlyName,
        string? DeviceType,
        string? CastId,
        string? Vendor,
        string? Os
    );

    private async Task MaterializeInterfaceMacsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Google Wifi reports the AP's own interface MACs obscured (real OUI, masked
        // device bytes). These MACs ARE the AP's identity, so each pass we (a) reconstruct
        // each interface's real MAC by IP + OUI and record it on the interface (once), and
        // (b) register the obscured MAC (always) and the real MAC (whenever known) as
        // fingerprints on the AP device, unioned with the AP's existing identity (its
        // google-wifi-device-id) in one resolve. Step (b) runs on EVERY pass — not only the
        // pass where reconstruction first succeeds — so a scanner/ARP record that carries
        // the same real MAC but first appears in a LATER batch still merges onto the AP.
        // (The prior version dropped an interface from the query the moment mac_address was
        // set, making the merge one-shot: if the real-MAC observer arrived after
        // reconstruction, the AP stayed split into two devices forever.)
        List<(string Device, string Interface, string? Ip, string? ObscuredMac, string? MacAddress)> rows =
            await conn.GetInterfaceObscuredMacRowsAsync(ct).ToListAsync(ct);
        // Reader closed — conn is free for the per-row lookups + writes below.

        foreach (IGrouping<string, (string Device, string Interface, string? Ip, string? ObscuredMac, string?
            MacAddress)> apGroup in rows.GroupBy(r => r.Device, StringComparer.Ordinal))
        {
            string device = apGroup.Key;
            List<Fingerprint> fps = [];

            foreach ((string _, string iface, string? ip, string? obscured, string? macAddress) in apGroup)
            {
                if (obscured is null)
                {
                    continue;
                }

                // The obscured MAC alone is a stable identity anchor for the AP.
                fps.Add(new Fingerprint(FingerprintType.ObscuredMac, obscured));

                // Prefer the real MAC already reconstructed on a prior pass; only
                // reconstruct when it is still missing. Either way, when the real MAC is
                // known it is fed into the resolve below EVERY pass (the normalizer
                // canonicalizes the stored colon form and the 12-hex form identically).
                string? realMac = NullIfBlank(macAddress);
                if (realMac is null && ip is not null && ObscuredMac.TryGetOui(obscured, out string oui))
                {
                    string? full = await TryReconstructMacAsync(conn, ip, oui, ct);
                    if (full is null)
                    {
                        DiscoveryMaterializerLog.InterfaceMacUnresolved(_logger, obscured, ip);
                    }
                    else
                    {
                        await conn.SetInterfaceMacAsync(device, iface, FormatMac(full), ct).ExecuteAsync(ct);
                        DiscoveryMaterializerLog.MacReconstructed(_logger, obscured, full);
                        realMac = full;
                    }
                }

                if (realMac is not null)
                {
                    fps.Add(new Fingerprint(FingerprintType.Mac, realMac));
                }
            }

            if (fps.Count == 0 || !Guid.TryParse(device, out Guid deviceId))
            {
                continue;
            }

            // Include the AP device's existing fingerprints so the resolve matches the
            // AP itself (via its google-wifi-device-id) AND any other device holding the
            // reconstructed real MAC — merging the two into one.
            List<(string FpType, string FpValue, string? Source, DateTimeOffset LastSeen)> existing =
                await conn.GetDeviceFingerprintsAsync(deviceId, ct).ToListAsync(ct);
            foreach ((string fpType, string fpValue, string? _, DateTimeOffset _) in existing)
            {
                fps.Add(new Fingerprint(fpType, fpValue));
            }

            await TryResolveDiscoveredAsync(conn, fps, source: "google-wifi", ct, logTag: "google-wifi-interface");
        }
    }

    // 12 lowercase hex (as stored in the known-MAC set) → "AA:BB:CC:DD:EE:FF", the
    // uppercase colon form the interface projection uses for every other MAC.
    private static string FormatMac(string hex12)
    {
        Span<char> buf = stackalloc char[17];
        int j = 0;
        for (int i = 0; i < 12; i += 2)
        {
            if (i > 0)
            {
                buf[j++] = ':';
            }

            buf[j++] = char.ToUpperInvariant(hex12[i]);
            buf[j++] = char.ToUpperInvariant(hex12[i + 1]);
        }

        return new string(buf);
    }

    // ── Row helpers ───────────────────────────────────────────────────────────

    private static bool HasAnyFingerprint(DiscoveredRow r) =>
        !string.IsNullOrEmpty(r.Mac)
     || !string.IsNullOrEmpty(r.OnvifSerial)
     || !string.IsNullOrEmpty(r.RokuSerial)
     || !string.IsNullOrEmpty(r.SnmpSerial)
     || !string.IsNullOrEmpty(r.SsdpUuid)
     || !string.IsNullOrEmpty(r.WsdUuid);

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}

internal static partial class DiscoveryMaterializerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped discovered row: MAC normalization failed ({Source}).")]
    public static partial void MacNormalizationFailed(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reconstructed obscured MAC {Obscured} to {Full}.")]
    public static partial void MacReconstructed(ILogger logger, string obscured, string full);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not reconstruct obscured MAC {Obscured}; no unique match.")]
    public static partial void MacUnresolved(ILogger logger, string obscured);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Could not reconstruct obscured interface MAC {Obscured} at {Ip}; no unique match."
    )]
    public static partial void InterfaceMacUnresolved(ILogger logger, string obscured, string ip);
}