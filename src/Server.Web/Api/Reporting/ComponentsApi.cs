using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Fleet-wide hardware component inventory: one row per (device, component) — board, CPU,
/// disk, fan, PSU, etc. (dmidecode/lshw/SNMP entPhysical). See HardwareApi for per-device
/// cumulative specs instead.
/// </summary>
public static class ComponentsApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["hostname"] = "coalesce(s.hostname, '')",
            ["class"] = "coalesce(c.class, '')",
        };

    public const string DefaultSort = "hostname";

    public static readonly IReadOnlySet<string> SortableColumns = SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/components", ListComponents)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListComponents(
        NpgsqlDataSource db,
        string? q,
        string? cls,
        string? sort,
        string? dir,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<ComponentListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) => QueryAsync(db, q, cls, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Components page model.</summary>
    public static async Task<(List<ComponentListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? search,
        string? cls,
        string? afterSortKey,
        string? afterDevice,
        string? afterComponent,
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
                c.device, s.hostname, c.hwcomponent, c.class, c.slot, c.description, c.vendor,
                c.model, c.serial, c.firmware, c.status, c.is_fru,
                {sortKeyCol} AS sort_key,
                COALESCE(s.friendly_name, s.hostname) AS friendly_name
            FROM proj_hardware_inventory c
                LEFT JOIN proj_systems s ON s.device = c.device
            WHERE ($1::text IS NULL OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(c.description, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(c.vendor, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(c.model, '') ILIKE '%' || $1 || '%')
              AND ($2::text IS NULL OR c.class = $2)
              AND ($3::text IS NULL OR (({sortKeyCol}, c.device, c.hwcomponent) {cmp} ($3, $4, $5)))
            ORDER BY {sortKeyCol} {direction}, c.device {direction}, c.hwcomponent {direction}
            LIMIT $6
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(search) ? null : search));
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(cls) ? null : cls));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterComponent));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<ComponentListItem> items = new();
        List<string> sortKeys = new();
        List<(string Device, string Component)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string device = reader.GetString(0);
                string component = reader.GetString(2);
                items.Add(
                    new ComponentListItem(
                        Device: device,
                        Hostname: GetStr(reader, 1),
                        Class: GetStr(reader, 3),
                        Slot: GetStr(reader, 4),
                        Description: GetStr(reader, 5),
                        Vendor: GetStr(reader, 6),
                        Model: GetStr(reader, 7),
                        Serial: GetStr(reader, 8),
                        Firmware: GetStr(reader, 9),
                        Status: GetStr(reader, 10),
                        IsFru: reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                        FriendlyName: GetStr(reader, 13)
                    )
                );
                sortKeys.Add(GetStr(reader, 12) ?? string.Empty);
                tiebreakers.Add((device, component));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string Component) lastTie = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], lastTie.Device, lastTie.Component);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record ComponentListItem(
    string Device,
    string? Hostname,
    string? Class,
    string? Slot,
    string? Description,
    string? Vendor,
    string? Model,
    string? Serial,
    string? Firmware,
    string? Status,
    bool? IsFru,
    string? FriendlyName
);