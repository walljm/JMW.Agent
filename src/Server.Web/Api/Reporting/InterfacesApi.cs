using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>Fleet-wide network interface inventory: one row per (device, interface).</summary>
public static class InterfacesApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListInterfacesAsync —
    /// the generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "hostname";

    /// <summary>
    /// Columns the interface list may be sorted by — hostname rides proj_devices' resolved
    /// identity column, name/speed the driving interface table (speed via the 0108 zero-padded
    /// text index). Sourced from the generated [SortableBy] allowlist so the UI cannot drift
    /// from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListInterfacesAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<InterfaceListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Interface)> tiebreakers = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? hostname, string? name, string? macAddress, string? obscuredMac,
            string? oui, string? ouiCountry, string? ipv4, string? ipv6, long? mtu, bool? up, bool? loopback,
            long? speedBps, string? duplex, string? type, string ifaceKey, string? sortKey, string? friendlyName)
            in conn.ListInterfacesAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                afterSortKey,
                afterDevice,
                afterInterface,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new InterfaceListItem(
                    Device: device,
                    Hostname: hostname,
                    Name: name,
                    MacAddress: macAddress,
                    ObscuredMac: obscuredMac,
                    Oui: oui,
                    OuiCountry: ouiCountry,
                    Ipv4: ipv4,
                    Ipv6: ipv6,
                    Mtu: mtu,
                    Up: up,
                    Loopback: loopback,
                    SpeedBps: speedBps,
                    Duplex: duplex,
                    Type: type,
                    FriendlyName: friendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
            tiebreakers.Add((device, ifaceKey));
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
    string? Type,
    string? FriendlyName
);