using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class PortsApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>
    /// Columns the port list may be sorted by. Only columns with a supporting index are exposed —
    /// see <c>proj_ports_port_idx</c>. All expressions are text (port zero-padded) so one cursor
    /// shape — (sort_key, device, listeningport) — covers every sort.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["device"] = "p.device",
            ["port"] = "lpad(p.port::text, 5, '0')",
        };

    public const string DefaultSort = "device";

    public static readonly IReadOnlySet<string> SortableColumns =
        SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/ports", ListPorts)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListPorts(
        NpgsqlDataSource db,
        string? after,
        int? port,
        string? proto,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<PortListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) => QueryAsync(db, port, proto, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Ports page model.</summary>
    public static async Task<(List<PortListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        int? port,
        string? proto,
        string? afterSortKey,
        string? afterDevice,
        string? afterListeningPort,
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
                p.device, s.hostname, p.listeningport, p.protocol, p.address, p.port,
                p.process_name, p.pid,
                {sortKeyCol} AS sort_key
            FROM proj_ports p
                LEFT JOIN proj_systems s ON s.device = p.device
            WHERE ($1::integer IS NULL OR p.port = $1)
              AND ($2::text IS NULL OR p.protocol = $2)
              AND ($3::text IS NULL OR (({sortKeyCol}, p.device, p.listeningport) {cmp} ($3, $4, $5)))
            ORDER BY {sortKeyCol} {direction}, p.device {direction}, p.listeningport {direction}
            LIMIT $6
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.NullableInteger(port));
        cmd.Parameters.Add(Param.Text(string.IsNullOrWhiteSpace(proto) ? null : proto));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterListeningPort));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<PortListItem> items = new();
        // sort_key[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string ListeningPort)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string device = reader.GetString(0);
                string listeningPort = reader.GetString(2);
                items.Add(
                    new PortListItem(
                        Device: device,
                        Hostname: GetStr(reader, 1),
                        Protocol: GetStr(reader, 3),
                        Address: GetStr(reader, 4),
                        Port: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        ProcessName: GetStr(reader, 6),
                        Pid: reader.IsDBNull(7) ? null : reader.GetInt64(7)
                    )
                );
                sortKeys.Add(GetStr(reader, 8) ?? string.Empty);
                tiebreakers.Add((device, listeningPort));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string ListeningPort) lastTie = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], lastTie.Device, lastTie.ListeningPort);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record PortListItem(
    string Device,
    string? Hostname,
    string? Protocol,
    string? Address,
    int? Port,
    string? ProcessName,
    long? Pid
);