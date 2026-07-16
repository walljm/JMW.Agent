using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>Fleet-wide network interface inventory: one row per (device, interface).</summary>
public static class InterfacesApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["hostname"] = "coalesce(s.hostname, '')",
            ["name"] = "coalesce(i.name, '')",
            ["speed"] = "coalesce(i.speed_bps, -1)",
        };

    public const string DefaultSort = "hostname";

    public static readonly IReadOnlySet<string> SortableColumns = SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/interfaces", ListInterfaces)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListInterfaces(
        NpgsqlDataSource db,
        string? q,
        string? sort,
        string? dir,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<InterfaceListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) => QueryAsync(db, q, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Interfaces page model.</summary>
    public static async Task<(List<InterfaceListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? search,
        string? afterSortKey,
        string? afterDevice,
        string? afterInterface,
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
                i.device, s.hostname, i.name, i.mac_address, i.obscured_mac,
                -- Obscured MACs (Google Wifi/OnHub firmware) preserve only the real OUI (first 6
                -- hex nibbles); the rest is fabricated. When there's no reconstructed real MAC,
                -- fall back to just that trustworthy prefix (same idiom as DeviceListApi.QueryAsync).
                COALESCE(
                    oui_vendor(i.mac_address),
                    oui_vendor(left(regexp_replace(lower(i.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui,
                COALESCE(
                    oui_country(i.mac_address),
                    oui_country(left(regexp_replace(lower(i.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
                ) AS oui_country,
                i.ipv4, i.ipv6, i.mtu, i.up, i.loopback, i.speed_bps, i.duplex, i.type,
                i.interface,
                {sortKeyCol} AS sort_key
            FROM proj_interfaces i
                LEFT JOIN proj_systems s ON s.device = i.device
            WHERE ($1::text IS NULL OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(i.name, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(i.ipv4, '') ILIKE '%' || $1 || '%')
              AND ($2::text IS NULL OR (({sortKeyCol}, i.device, i.interface) {cmp} ($2, $3, $4)))
            ORDER BY {sortKeyCol} {direction}, i.device {direction}, i.interface {direction}
            LIMIT $5
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(search) ? null : search));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterInterface));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<InterfaceListItem> items = new();
        List<string> sortKeys = new();
        List<(string Device, string Interface)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string device = reader.GetString(0);
                string ifaceKey = reader.GetString(15);
                items.Add(
                    new InterfaceListItem(
                        Device: device,
                        Hostname: GetStr(reader, 1),
                        Name: GetStr(reader, 2),
                        MacAddress: GetStr(reader, 3),
                        ObscuredMac: GetStr(reader, 4),
                        Oui: GetStr(reader, 5),
                        OuiCountry: GetStr(reader, 6),
                        Ipv4: GetStr(reader, 7),
                        Ipv6: GetStr(reader, 8),
                        Mtu: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        Up: reader.IsDBNull(10) ? null : reader.GetBoolean(10),
                        Loopback: reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                        SpeedBps: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                        Duplex: GetStr(reader, 13),
                        Type: GetStr(reader, 14)
                    )
                );
                sortKeys.Add(GetStr(reader, 16) ?? string.Empty);
                tiebreakers.Add((device, ifaceKey));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string Interface) lastTie = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], lastTie.Device, lastTie.Interface);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record InterfaceListItem(
    string Device,
    string? Hostname,
    string? Name,
    string? MacAddress,
    string? ObscuredMac,
    string? Oui,
    string? OuiCountry,
    string? Ipv4,
    string? Ipv6,
    long? Mtu,
    bool? Up,
    bool? Loopback,
    long? SpeedBps,
    string? Duplex,
    string? Type
);