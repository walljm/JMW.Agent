using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class PortsApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListPortsAsync — the
    /// generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "device";

    /// <summary>
    /// Columns the port list may be sorted by. Only columns with a supporting index are exposed —
    /// see <c>proj_ports_port_idx</c>. All expressions are text (port zero-padded) so one cursor
    /// shape — (sort_key, device, listeningport) — covers every sort. Sourced from the generated
    /// [SortableBy] allowlist so the UI cannot drift from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListPortsAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<PortListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string ListeningPort)> tiebreakers = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? hostname, string listeningPort, string? protocol, string? address,
            int? portNumber, string? processName, long? pid, string? sortKey, string? friendlyName)
            in conn.ListPortsAsync(
                port,
                string.IsNullOrWhiteSpace(proto) ? null : proto,
                afterSortKey,
                afterDevice,
                afterListeningPort,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new PortListItem(
                    Device: device,
                    Hostname: hostname,
                    Protocol: protocol,
                    Address: address,
                    Port: portNumber,
                    ProcessName: processName,
                    Pid: pid,
                    FriendlyName: friendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
            tiebreakers.Add((device, listeningPort));
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
}

public sealed record PortListItem(
    string Device,
    string? Hostname,
    string? Protocol,
    string? Address,
    int? Port,
    string? ProcessName,
    long? Pid,
    string? FriendlyName
);