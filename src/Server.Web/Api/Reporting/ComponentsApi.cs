using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

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

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListComponentsAsync —
    /// the generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "hostname";

    /// <summary>
    /// Columns the component list may be sorted by — hostname rides proj_devices' resolved
    /// identity column, class the driving component table (0104 index). Sourced from the
    /// generated [SortableBy] allowlist so the UI cannot drift from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListComponentsAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<ComponentListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();
        List<(string Device, string Component)> tiebreakers = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? hostname, string component, string? rowClass, string? slot,
            string? description, string? vendor, string? model, string? serial, string? firmware,
            string? status, bool? isFru, string? sortKey, string? friendlyName)
            in conn.ListComponentsAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                string.IsNullOrWhiteSpace(cls) ? null : cls,
                afterSortKey,
                afterDevice,
                afterComponent,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new ComponentListItem(
                    Device: device,
                    Hostname: hostname,
                    Class: rowClass,
                    Slot: slot,
                    Description: description,
                    Vendor: vendor,
                    Model: model,
                    Serial: serial,
                    Firmware: firmware,
                    Status: status,
                    IsFru: isFru,
                    FriendlyName: friendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
            tiebreakers.Add((device, component));
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