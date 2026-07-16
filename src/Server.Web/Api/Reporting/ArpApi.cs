using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ArpApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>
    /// Columns the ARP list may be sorted by. Only columns with a supporting index are exposed —
    /// see <c>proj_device_arp_mac_idx</c>. One cursor shape — (sort_key, device, arp) — covers
    /// every sort.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["device"] = "a.device",
            ["mac"] = "coalesce(a.mac, '')",
        };

    public const string DefaultSort = "device";

    public static readonly IReadOnlySet<string> SortableColumns =
        SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/arp", ListArp)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListArp(
        NpgsqlDataSource db,
        string? after,
        string? q,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<ArpListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) => QueryAsync(db, q, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Arp page model.</summary>
    public static async Task<(List<ArpListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? search,
        string? afterSortKey,
        string? afterDevice,
        string? afterArp,
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
            SELECT
                a.device, s.hostname AS observer_hostname, a.arp AS ip, a.mac, a.iface, a.state,
                CASE WHEN df.device_id IS NULL THEN NULL ELSE d.device_id END AS resolved_device_id,
                rs.hostname AS resolved_hostname,
                oui_vendor(a.mac) AS oui, oui_country(a.mac) AS oui_country,
                {sortKeyCol} AS sort_key,
                COALESCE(rs.friendly_name, rs.hostname) AS resolved_friendly_name
            FROM proj_device_arp a
                LEFT JOIN proj_systems s ON s.device = a.device
                LEFT JOIN device_fingerprints df ON df.fp_type = 'mac' AND df.fp_value = a.mac
                LEFT JOIN devices d ON d.device_id = df.device_id
                LEFT JOIN proj_systems rs ON rs.device = d.device_id::text
            WHERE ($1::text IS NULL OR a.mac ILIKE '%' || $1 || '%' OR a.arp ILIKE '%' || $1 || '%')
              AND ($2::text IS NULL OR (({sortKeyCol}, a.device, a.arp) {cmp} ($2, $3, $4)))
            ORDER BY {sortKeyCol} {direction}, a.device {direction}, a.arp {direction}
            LIMIT $5
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(search) ? null : search));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterArp));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<ArpListItem> items = new();
        // sort_key[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Arp)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string device = reader.GetString(0);
                string ip = reader.GetString(2);
                items.Add(
                    new ArpListItem(
                        Device: device,
                        ObserverHostname: GetStr(reader, 1),
                        Ip: ip,
                        Mac: GetStr(reader, 3),
                        Iface: GetStr(reader, 4),
                        State: GetStr(reader, 5),
                        ResolvedDeviceId: reader.IsDBNull(6) ? null : reader.GetGuid(6).ToString(),
                        ResolvedHostname: GetStr(reader, 7),
                        Oui: GetStr(reader, 8),
                        OuiCountry: GetStr(reader, 9),
                        ResolvedFriendlyName: GetStr(reader, 11)
                    )
                );
                sortKeys.Add(GetStr(reader, 10) ?? string.Empty);
                tiebreakers.Add((device, ip));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string Arp) lastTie = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], lastTie.Device, lastTie.Arp);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record ArpListItem(
    string Device,
    string? ObserverHostname,
    string Ip,
    string? Mac,
    string? Iface,
    string? State,
    string? ResolvedDeviceId,
    string? ResolvedHostname,
    string? Oui,
    string? OuiCountry,
    string? ResolvedFriendlyName
);