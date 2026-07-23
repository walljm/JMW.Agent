using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ContainersApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListContainersAsync — the
    /// generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "device";

    /// <summary>
    /// Columns the container list may be sorted by. Only columns with a supporting index are
    /// exposed — see <c>proj_containers_state_idx</c>. One cursor shape —
    /// (sort_key, device, container) — covers every sort. Sourced from the generated
    /// [SortableBy] allowlist so the UI cannot drift from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListContainersAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<ContainerListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Container)> tiebreakers = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? hostname, string container, string? name, string? rowImage,
            string? rowState, string? health, double? cpuPct, long? memUsageBytes, long? restartCount,
            string? composeProject, string? composeService, string? restartPolicy, string? sortKey,
            string? friendlyName)
            in conn.ListContainersAsync(
                state,
                image,
                afterSortKey,
                afterDevice,
                afterContainer,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new ContainerListItem(
                    Device: device,
                    Hostname: hostname,
                    Container: container,
                    Name: name,
                    Image: rowImage,
                    State: rowState,
                    Health: health,
                    CpuPct: cpuPct,
                    MemUsageBytes: memUsageBytes,
                    RestartCount: restartCount,
                    ComposeProject: composeProject,
                    ComposeService: composeService,
                    RestartPolicy: restartPolicy,
                    FriendlyName: friendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
            tiebreakers.Add((device, container));
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
    string? RestartPolicy,
    string? FriendlyName
);