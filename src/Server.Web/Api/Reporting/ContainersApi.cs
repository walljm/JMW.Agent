using JMW.Discovery.Server.Data;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ContainersApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>
    /// Columns the container list may be sorted by. Only columns with a supporting index are
    /// exposed — see <c>proj_containers_state_idx</c>. One cursor shape —
    /// (sort_key, device, container) — covers every sort.
    /// </summary>
    private static readonly Dictionary<string, string> SortExpressions =
        new(StringComparer.Ordinal)
        {
            ["device"] = "c.device",
            ["state"] = "coalesce(c.state, '')",
        };

    public const string DefaultSort = "device";

    public static readonly IReadOnlySet<string> SortableColumns =
        SortExpressions.Keys.ToHashSet(StringComparer.Ordinal);

    public static bool IsSortable(string? sort) => sort is not null && SortExpressions.ContainsKey(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/containers", ListContainers)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListContainers(
        NpgsqlDataSource db,
        string? after,
        string? state,
        string? image,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<ContainerListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 3,
            fetch: (parts, lim) => QueryAsync(db, state, image, parts?[0], parts?[1], parts?[2], lim, ct, sort, dir)
        );

    /// <summary>Shared query used by both the JSON endpoint and the Containers page model.</summary>
    public static async Task<(List<ContainerListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? state,
        string? image,
        string? afterSortKey,
        string? afterDevice,
        string? afterContainer,
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
                c.device, s.hostname, c.container, c.name, c.image, c.state, c.health, c.cpu_pct,
                c.mem_usage_bytes, c.restart_count, c.compose_project, c.compose_service, c.restart_policy,
                {sortKeyCol} AS sort_key
            FROM proj_containers c
                LEFT JOIN proj_systems s ON s.device = c.device
            WHERE ($1::text IS NULL OR c.state = $1)
              AND ($2::text IS NULL OR c.image LIKE '%' || $2 || '%')
              AND ($3::text IS NULL OR (({sortKeyCol}, c.device, c.container) {cmp} ($3, $4, $5)))
            ORDER BY {sortKeyCol} {direction}, c.device {direction}, c.container {direction}
            LIMIT $6
            """;

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.Text(state));
        cmd.Parameters.Add(Param.Text(image));
        cmd.Parameters.Add(Param.Text(afterSortKey));
        cmd.Parameters.Add(Param.Text(afterDevice));
        cmd.Parameters.Add(Param.Text(afterContainer));
        cmd.Parameters.Add(Param.Integer(limit + 1));

        List<ContainerListItem> items = new();
        // sort_key[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Container)> tiebreakers = new();
        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                string device = reader.GetString(0);
                string container = reader.GetString(2);
                items.Add(
                    new ContainerListItem(
                        Device: device,
                        Hostname: GetStr(reader, 1),
                        Container: container,
                        Name: GetStr(reader, 3),
                        Image: GetStr(reader, 4),
                        State: GetStr(reader, 5),
                        Health: GetStr(reader, 6),
                        CpuPct: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                        MemUsageBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        RestartCount: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        ComposeProject: GetStr(reader, 10),
                        ComposeService: GetStr(reader, 11),
                        RestartPolicy: GetStr(reader, 12)
                    )
                );
                sortKeys.Add(GetStr(reader, 13) ?? string.Empty);
                tiebreakers.Add((device, container));
            }
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            tiebreakers.RemoveAt(tiebreakers.Count - 1);
            (string Device, string Container) lastTie = tiebreakers[^1];
            nextCursor = KeysetCursor.EncodeParts(sortKeys[^1], lastTie.Device, lastTie.Container);
        }

        return (items, nextCursor);
    }

    private static string? GetStr(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

public sealed record ContainerListItem(
    string Device,
    string? Hostname,
    string Container,
    string? Name,
    string? Image,
    string? State,
    string? Health,
    double? CpuPct,
    long? MemUsageBytes,
    long? RestartCount,
    string? ComposeProject,
    string? ComposeService,
    string? RestartPolicy
);