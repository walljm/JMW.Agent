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
    /// is the SQL sort expression. ORDER BY / cursor use an allowlist (never user text) so this
    /// is injection-safe while every value stays a bound parameter. All expressions are text so
    /// one keyset cursor shape — (sort_key, device) — covers every column; COALESCE keeps
    /// null-valued rows reachable across pages.
    ///
    /// The identity columns (hostname/friendly_name/ip/mac/vendor) are context-derivation finals
    /// on proj_devices — the query's DRIVING table — with matching expression indexes (migration
    /// 0106), so those sorts are index-driven (a LEFT-JOINed column's ORDER BY never is; see
    /// context-derivations.md §3.3). os (two joined proj_systems columns), status, and last_seen
    /// sort the filtered set — rare sorts, trivial at current scale.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["hostname"] = "coalesce(pdv.hostname, '')",
            ["friendly_name"] = "coalesce(pdv.friendly_name, '')",
            // ip_sort_key makes lexical text order match numeric IP order (192.168.1.9 < .10 < .100).
            ["ip"] = "ip_sort_key(pdv.ip)",
            ["mac"] = "coalesce(pdv.mac, '')",
            ["vendor"] = "coalesce(pdv.vendor, '')",
            ["os"] = "coalesce(s.os_family, '') || ' ' || coalesce(s.os_distro, '')",
            ["status"] = "d.management_status",
            ["last_seen"] =
                "coalesce(to_char(d.last_seen at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US'), '')",
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
        string sql = BuildSql(sort, dir);

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

    /// <summary>Builds the list SQL for a sort/direction. Extracted so the EXPLAIN-based index
    /// test (ReportPlanTests) can assert the exact production query's plan.</summary>
    public static string BuildSql(string? sort, string? dir)
    {
        string sortKeyCol = sort is not null && SortExpressions.TryGetValue(sort, out string? expr)
            ? expr
            : SortExpressions[DefaultSort];
        bool descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        string cmp = descending ? "<" : ">";
        string direction = descending ? "DESC" : "ASC";

        // proj_devices (pdv) DRIVES the query: the identity sort columns live on it, so the
        // keyset WHERE + ORDER BY (both entirely over pdv for the indexed sorts — device is the
        // tiebreaker AND the PK) push into the 0106 expression indexes; visible_devices and the
        // display joins are probed per scanned row. Every device has a pdv row from creation
        // (DeviceRegistry.CreateDeviceAsync) + the engine's per-pass backfill, so driving from
        // pdv never hides a device. The obscured-MAC fingerprint is the one remaining lateral —
        // deliberately not materialized (registry-only display fallback, no sort).
        string sql = $"""
            WITH device_sources AS (
                SELECT device_id, source FROM device_discovery_sources
            )
            SELECT
                d.device_id,
                -- The real, agent-reported OS hostname only — null for a passively-discovered
                -- device with no agent. Never backfilled from friendly-name-ish sources.
                s.hostname,
                -- Context-derivation finals (identity-* — see ContextDerivationLibrary): the
                -- resolved display rollup, best identity IP, and newest MAC, recomputed
                -- set-based on ingest instead of per-row laterals here.
                pdv.friendly_name,
                pdv.ip,
                pdv.mac,
                lower(dmac_obs.fp_value) AS obscured_mac,
                pdv.vendor,
                -- When the real MAC never reconstructs (ObscuredMac.Pick found no unique
                -- candidate), the obscured value still carries a trustworthy OUI in its first
                -- 6 hex digits (see ObscuredMac.cs) — fall back so vendor still resolves.
                COALESCE(
                    oui_vendor(pdv.mac),
                    oui_vendor(left(regexp_replace(lower(dmac_obs.fp_value), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui,
                COALESCE(
                    oui_country(pdv.mac),
                    oui_country(left(regexp_replace(lower(dmac_obs.fp_value), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui_country,
                s.os_family,
                s.os_distro,
                d.management_status,
                src_agg.sources,
                d.last_seen,
                {sortKeyCol} AS sort_key
            FROM proj_devices pdv
                JOIN visible_devices d ON d.device_id::text = pdv.device
                LEFT JOIN proj_systems s ON s.device = pdv.device
                LEFT JOIN LATERAL (
                    SELECT fp_value FROM device_fingerprints
                    WHERE device_id = d.device_id AND fp_type = 'obscured-mac'
                    ORDER BY last_seen DESC LIMIT 1) dmac_obs ON TRUE
                LEFT JOIN LATERAL (
                    SELECT string_agg(DISTINCT ds.source, ',' ORDER BY ds.source) AS sources
                    FROM device_sources ds
                    WHERE ds.device_id = d.device_id) src_agg ON TRUE
            WHERE ($1::text IS NULL OR d.management_status = $1)
              AND ($2::text IS NULL OR EXISTS (
                    SELECT 1 FROM device_sources ds WHERE ds.device_id = d.device_id AND ds.source = $2))
              AND ($3::text IS NULL OR s.os_family = $3)
              AND ($4::text IS NULL OR pdv.vendor = $4)
              AND ($5::text IS NULL OR coalesce(pdv.friendly_name, '') ILIKE '%' || $5 || '%'
                    OR EXISTS (SELECT 1 FROM device_fingerprints qf
                               WHERE qf.device_id = d.device_id AND qf.fp_value ILIKE '%' || $5 || '%'))
              AND ($6::text IS NULL OR (({sortKeyCol}, pdv.device) {cmp} ($6, $7)))
            ORDER BY {sortKeyCol} {direction}, pdv.device {direction}
            LIMIT $8
            """;

        return sql;
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