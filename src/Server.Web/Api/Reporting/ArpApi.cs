using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ArpApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListArpAsync — the
    /// generated command text falls back to that column for an unrecognized sort. Default is IP
    /// so entries from every observing device interleave by neighbor address (a router's ARP
    /// cache sorted by the observer's own UUID buries whole observers on later pages — e.g.
    /// core's rows never surfaced on page 1).</summary>
    public const string DefaultSort = "ip";

    /// <summary>
    /// Columns the ARP list may be sorted by. One cursor shape — (sort_key, device, arp) —
    /// covers every sort. Sourced from the generated [SortableBy] allowlist so the UI cannot
    /// drift from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListArpAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<ArpListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Arp)> tiebreakers = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? observerHostname, string ip, string? mac, string? iface,
            string? state, Guid? resolvedDeviceId, string? resolvedHostname, string? oui, string? ouiCountry,
            string? sortKey, string? resolvedFriendlyName)
            in conn.ListArpAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                afterSortKey,
                afterDevice,
                afterArp,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new ArpListItem(
                    Device: device,
                    ObserverHostname: observerHostname,
                    Ip: ip,
                    Mac: mac,
                    Iface: iface,
                    State: state,
                    ResolvedDeviceId: resolvedDeviceId?.ToString(),
                    ResolvedHostname: resolvedHostname,
                    Oui: oui,
                    OuiCountry: ouiCountry,
                    ResolvedFriendlyName: resolvedFriendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
            tiebreakers.Add((device, ip));
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