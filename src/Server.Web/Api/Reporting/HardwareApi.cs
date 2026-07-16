using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Fleet-wide hardware inventory: one row per device, the cumulative specs (CPU, installed
/// memory, total disk capacity) rather than per-component detail (see ComponentsApi for that).
/// </summary>
public static class HardwareApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["hostname"] = "coalesce(s.hostname, '')",
            ["vendor"] = "coalesce(h.system_vendor, '')",
            ["cpu"] = "coalesce(h.cpu_model, '')",
        };

    public const string DefaultSort = "hostname";

    public static readonly IReadOnlySet<string> SortableColumns = SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/hardware", ListHardware)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListHardware(
        NpgsqlDataSource db,
        string? q,
        string? sort,
        string? dir,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<HardwareListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryAsync(db, q, parts?[0], parts?[1], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Hardware page model.</summary>
    public static async Task<(List<HardwareListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? search,
        string? afterSortKey,
        string? afterDevice,
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
                h.device, s.hostname, h.system_vendor, h.system_model, h.system_serial,
                h.bios_version, h.virtualization, h.cpu_model, h.cpu_vendor, h.cpu_cores,
                h.cpu_logical_cores, h.cpu_mhz, h.total_mem_bytes, disks.total_bytes,
                {sortKeyCol} AS sort_key,
                COALESCE(s.friendly_name, s.hostname) AS friendly_name
            FROM proj_hardware h
                LEFT JOIN proj_systems s ON s.device = h.device
                LEFT JOIN LATERAL (
                    SELECT SUM(d.size_bytes) AS total_bytes FROM proj_disks d WHERE d.device = h.device
                ) disks ON TRUE
            WHERE ($1::text IS NULL OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(h.system_vendor, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(h.system_model, '') ILIKE '%' || $1 || '%'
                    OR COALESCE(h.cpu_model, '') ILIKE '%' || $1 || '%')
              AND ($2::text IS NULL OR (({sortKeyCol}, h.device) {cmp} ($2, $3)))
            ORDER BY {sortKeyCol} {direction}, h.device {direction}
            LIMIT $4
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(search) ? null : search));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<HardwareListItem> items = new();
        List<string> sortKeys = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(
                    new HardwareListItem(
                        Device: reader.GetString(0),
                        Hostname: GetStr(reader, 1),
                        SystemVendor: GetStr(reader, 2),
                        SystemModel: GetStr(reader, 3),
                        SystemSerial: GetStr(reader, 4),
                        BiosVersion: GetStr(reader, 5),
                        Virtualization: GetStr(reader, 6),
                        CpuModel: GetStr(reader, 7),
                        CpuVendor: GetStr(reader, 8),
                        CpuCores: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        CpuLogicalCores: reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        CpuMhz: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        TotalMemBytes: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                        TotalStorageBytes: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                        FriendlyName: GetStr(reader, 15)
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
            nextCursor = KeysetCursor.Encode(sortKeys[^1], items[^1].Device);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record HardwareListItem(
    string Device,
    string? Hostname,
    string? SystemVendor,
    string? SystemModel,
    string? SystemSerial,
    string? BiosVersion,
    string? Virtualization,
    string? CpuModel,
    string? CpuVendor,
    long? CpuCores,
    long? CpuLogicalCores,
    double? CpuMhz,
    long? TotalMemBytes,
    long? TotalStorageBytes,
    string? FriendlyName
);