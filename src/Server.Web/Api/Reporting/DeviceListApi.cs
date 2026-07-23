using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class DeviceListApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>
    /// Columns the host list may be sorted by. The key is the stable API/UI token; the value
    /// is the SQL sort expression over the decorated result columns. ORDER BY / cursor use an
    /// allowlist (never user text) so this is injection-safe while every value stays a bound
    /// parameter. All expressions are text so one keyset cursor shape — (sort_key, device_id)
    /// — covers every column; COALESCE keeps null-valued rows reachable across pages.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["hostname"] = "coalesce(hostname, '')",
            ["friendly_name"] = "coalesce(friendly_name, '')",
            // ip_sort_key makes lexical text order match numeric IP order (192.168.1.9 < .10 < .100).
            ["ip"] = "ip_sort_key(ip)",
            ["mac"] = "coalesce(mac, '')",
            ["vendor"] = "coalesce(vendor, '')",
            ["os"] = "coalesce(os_family, '') || ' ' || coalesce(os_distro, '')",
            ["status"] = "management_status",
            ["last_seen"] =
                "coalesce(to_char(last_seen at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US'), '')",
        };

    public const string DefaultSort = "ip";

    /// <summary>The sortable column keys, for <see cref="JMW.Discovery.Server.UI.GridState" />'s
    /// allowlist.</summary>
    public static readonly IReadOnlySet<string> SortableColumns = SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/devices", ListDevices)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static async Task<IResult> ListDevices(
        NpgsqlDataSource db,
        string? after,
        string? status,
        string? source,
        string? os,
        string? vendor,
        string? q,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        string? afterSortKey = null;
        string? afterDeviceId = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecode(after, out string sortKey, out string deviceId))
            {
                return ApiError.InvalidCursor();
            }

            afterSortKey = sortKey;
            afterDeviceId = deviceId;
        }

        (List<DeviceReportItem> items, string? nextCursor) = await QueryAsync(
            db,
            status,
            source,
            os,
            vendor,
            q,
            afterSortKey,
            afterDeviceId,
            limit,
            ct,
            sort,
            dir
        );

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    /// <summary>
    /// Shared query used by both the JSON endpoint and the Devices page model.
    /// Fetches limit+1 rows to determine whether a next page exists. The sort column is
    /// resolved from <see cref="SortExpressions" /> (default hostname); the keyset comparison
    /// and ORDER BY use that expression with device_id as a stable, unique tiebreaker so pages
    /// never skip or repeat rows. Descending flips both the tuple comparison and ORDER BY.
    /// </summary>
    public static async Task<(List<DeviceReportItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? status,
        string? source,
        string? os,
        string? vendor,
        string? search,
        string? afterSortKey,
        string? afterDeviceId,
        int limit,
        CancellationToken ct,
        string? sort = null,
        string? dir = null
    )
    {
        string sortKeyCol = sort is not null && SortExpressions.TryGetValue(sort, out string? expr)
            ? expr
            : SortExpressions[DefaultSort];
        bool descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        string cmp = descending ? "<" : ">";
        string direction = descending ? "DESC" : "ASC";

        string sql = $"""
            {BaseCte}
            SELECT
                device_id, hostname, friendly_name, ip, mac, obscured_mac, vendor, oui, oui_country, os_family,
                os_distro,
                management_status, sources, last_seen,
                {sortKeyCol} AS sort_key
            FROM decorated
            WHERE ($6::text IS NULL OR (({sortKeyCol}, device_id::text) {cmp} ($6, $7)))
            ORDER BY {sortKeyCol} {direction}, device_id::text {direction}
            LIMIT $8
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        AddText(cmd, StatusFilter.NormalizeStatus(status));
        AddText(cmd, Blank(source));
        AddText(cmd, Blank(os));
        AddText(cmd, Blank(vendor));
        AddText(cmd, Blank(search));
        AddText(cmd, afterSortKey);
        AddText(cmd, afterDeviceId);
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<DeviceReportItem> items = new();
        // sort_key[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(
                    new DeviceReportItem(
                        DeviceId: reader.GetGuid(0).ToString(),
                        Hostname: GetStr(reader, 1),
                        FriendlyName: GetStr(reader, 2),
                        Ip: GetStr(reader, 3),
                        Mac: GetStr(reader, 4),
                        ObscuredMac: GetStr(reader, 5),
                        Vendor: GetStr(reader, 6),
                        Oui: GetStr(reader, 7),
                        OuiCountry: GetStr(reader, 8),
                        OsFamily: GetStr(reader, 9),
                        OsDistro: GetStr(reader, 10),
                        ManagementStatus: reader.GetString(11),
                        Sources: SplitSources(GetStr(reader, 12)),
                        LastSeen: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13).UtcDateTime
                    )
                );
                sortKeys.Add(GetStr(reader, 14) ?? string.Empty);
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            nextCursor = KeysetCursor.Encode(sortKeys[^1], items[^1].DeviceId);
        }

        return (items, nextCursor);
    }

    private static void AddText(NpgsqlCommand cmd, string? value) =>
        cmd.Parameters.Add(Param.Text(value));

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] SplitSources(string? sources) =>
        string.IsNullOrEmpty(sources)
            ? Array.Empty<string>()
            : sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Filters + per-row decoration (best IP, OUI, sources). The sort/keyset/LIMIT are applied by
    // the outer query in QueryAsync so every column — including the computed best IP — is sortable.
    private const string BaseCte = """
        WITH device_sources AS (
            -- The full "observed by" set per device (identifying fingerprint source + passive
            -- ARP/DHCP/scanner observers, mapped to short slugs). Shared with the dashboard
            -- composition breakdown via the device_discovery_sources view (migration 0096) so the
            -- scanner-slug map isn't duplicated here.
            SELECT device_id, source FROM device_discovery_sources
        ),
        filtered AS (
            SELECT
                d.device_id,
                -- The real, agent-reported OS hostname only — null for a passively-discovered
                -- device with no agent. Never backfilled from friendly-name-ish sources.
                s.hostname,
                -- Display rollup, in priority order: an operator-set/promoted friendly name
                -- (proj_systems.friendly_name), else the best friendly-name-ish value any
                -- observer has recorded for this device's MAC in proj_discovered (agentless
                -- devices not yet promoted), else the real hostname above.
                COALESCE(s.friendly_name, disc.name, s.hostname) AS friendly_name,
                lower(dmac.fp_value) AS mac,
                lower(dmac_obs.fp_value) AS obscured_mac,
                -- pdv.vendor is the unified cross-protocol vendor (DeviceVendorDerivation fans in
                -- hardware/BACnet/Modbus/Google Wifi). hw.system_vendor stays as a fallback only
                -- for devices whose derivation hasn't re-run since this field was added — once
                -- every agent re-reports at least once, pdv.vendor alone would suffice.
                COALESCE(pdv.vendor, hw.system_vendor) AS vendor,
                s.os_family,
                s.os_distro,
                s.last_seen_ip,
                d.management_status,
                -- Last seen = the newest fingerprint sighting. device_fingerprints.last_seen is
                -- stamped on every resolution (DeviceRegistry.UpsertFingerprintsAsync), so it
                -- reflects true recency for every device — passively-discovered (ARP-/SSH-/scanner-
                -- only) AND managed. We deliberately do NOT prefer proj_systems.updated_at: that
                -- only moves on a data change, so it goes stale on a live-but-static managed device
                -- and would misreport it as long-gone. This is the same signal the liveness-window
                -- filter (visible_devices) uses, so the shown time and the hide decision agree.
                (SELECT max(df.last_seen) FROM device_fingerprints df WHERE df.device_id = d.device_id)
                    AS last_seen
            FROM visible_devices d
                LEFT JOIN proj_systems  s   ON s.device   = d.device_id::text
                LEFT JOIN proj_hardware hw  ON hw.device  = d.device_id::text
                LEFT JOIN proj_devices  pdv ON pdv.device = d.device_id::text
                LEFT JOIN LATERAL (
                    SELECT fp_value FROM device_fingerprints
                    WHERE device_id = d.device_id AND fp_type = 'mac'
                    ORDER BY last_seen DESC LIMIT 1) dmac ON TRUE
                LEFT JOIN LATERAL (
                    SELECT fp_value FROM device_fingerprints
                    WHERE device_id = d.device_id AND fp_type = 'obscured-mac'
                    ORDER BY last_seen DESC LIMIT 1) dmac_obs ON TRUE
                LEFT JOIN LATERAL (
                    -- pd.obscured_mac IS NULL: exclude Google Wifi/OnHub rows whose mac was
                    -- filled in by obscured-MAC reconstruction rather than direct observation —
                    -- a stale mDNS advertisement can reconstruct to a totally different device's
                    -- MAC, and that name/model belongs to the cast-id identity, not this one
                    -- (MaterializeObscuredMacsAsync already promotes it correctly, keyed off
                    -- obscured-mac/cast-id, never the reconstructed MAC).
                    SELECT COALESCE(pd.friendly_name, pd.hostname) AS name
                    FROM proj_discovered pd
                    WHERE pd.mac = dmac.fp_value
                      AND pd.obscured_mac IS NULL
                      AND COALESCE(pd.friendly_name, pd.hostname) IS NOT NULL
                    ORDER BY pd.updated_at DESC LIMIT 1) disc ON TRUE
            WHERE ($1::text IS NULL OR d.management_status = $1)
              AND ($2::text IS NULL OR EXISTS (
                    SELECT 1 FROM device_sources ds WHERE ds.device_id = d.device_id AND ds.source = $2))
              AND ($3::text IS NULL OR s.os_family = $3)
              AND ($4::text IS NULL OR COALESCE(pdv.vendor, hw.system_vendor) = $4)
              AND ($5::text IS NULL OR COALESCE(s.friendly_name, disc.name, s.hostname, '') ILIKE '%' || $5 || '%'
                    OR EXISTS (SELECT 1 FROM device_fingerprints qf
                               WHERE qf.device_id = d.device_id AND qf.fp_value ILIKE '%' || $5 || '%'))
        ),
        decorated AS (
            SELECT
                f.device_id,
                f.hostname,
                f.friendly_name,
                ip_best.ip,
                f.mac,
                f.obscured_mac,
                f.vendor,
                -- When the real MAC never reconstructs (ObscuredMac.Pick found no unique ARP/DHCP
                -- candidate), the obscured value still carries a trustworthy OUI in its first 6 hex
                -- digits (see ObscuredMac.cs) — fall back to that so vendor still resolves.
                COALESCE(
                    oui_vendor(f.mac),
                    oui_vendor(left(regexp_replace(lower(f.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui,
                COALESCE(
                    oui_country(f.mac),
                    oui_country(left(regexp_replace(lower(f.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui_country,
                f.os_family,
                f.os_distro,
                f.management_status,
                src_agg.sources,
                f.last_seen
            FROM filtered f
                LEFT JOIN LATERAL (
                    SELECT cand.ip
                    FROM (
                        SELECT split_part(i.ipv4, '/', 1) AS ip, i.updated_at AS seen
                        FROM proj_interfaces i
                        WHERE i.device = f.device_id::text AND i.ipv4 IS NOT NULL
                        UNION ALL
                        SELECT f.last_seen_ip, f.last_seen WHERE f.last_seen_ip IS NOT NULL
                        UNION ALL
                        SELECT a.arp, a.updated_at
                        FROM proj_device_arp a
                        WHERE a.mac = f.mac AND a.arp IS NOT NULL
                        UNION ALL
                        -- The connect-IP every network scanner records for its sighting
                        -- (ARP/SSH-banner/HTTP-banner/ping-sweep all land in proj_discovered), so
                        -- an ARP-/SSH-/HTTP-only host that has no interface, last_seen_ip, or
                        -- proj_device_arp row still shows the address we reached it at. Matched by
                        -- MAC (proj_discovered.device is the OBSERVER, not this device) with the
                        -- same obscured_mac IS NULL guard the friendly-name join uses, so a
                        -- reconstructed-MAC sighting can't smear another identity's IP onto this
                        -- row. ip_identity_rank below still filters out non-identity addresses.
                        SELECT pdi.discovered, pdi.updated_at
                        FROM proj_discovered pdi
                        WHERE pdi.mac = f.mac AND pdi.obscured_mac IS NULL AND pdi.discovered IS NOT NULL
                    ) cand
                    WHERE cand.ip IS NOT NULL AND ip_identity_rank(cand.ip) < 99
                    ORDER BY (cand.ip LIKE '%:%'), ip_identity_rank(cand.ip), cand.seen DESC NULLS LAST
                    LIMIT 1) ip_best ON TRUE
                LEFT JOIN LATERAL (
                    SELECT string_agg(DISTINCT ds.source, ',' ORDER BY ds.source) AS sources
                    FROM device_sources ds
                    WHERE ds.device_id = f.device_id) src_agg ON TRUE
        )
        """;
}

public sealed record DeviceReportItem(
    string DeviceId,
    string? Hostname,
    string? FriendlyName,
    string? Ip,
    string? Mac,
    string? ObscuredMac,
    string? Vendor,
    string? Oui,
    string? OuiCountry,
    string? OsFamily,
    string? OsDistro,
    string ManagementStatus,
    IReadOnlyList<string> Sources,
    DateTime? LastSeen
);