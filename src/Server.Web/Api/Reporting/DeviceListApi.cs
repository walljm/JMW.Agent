using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class DeviceListApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListDeviceReportAsync —
    /// the generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "ip";

    /// <summary>
    /// Columns the host list may be sorted by, for <see cref="JMW.Discovery.Server.UI.GridState" />'s
    /// allowlist. The identity columns (hostname/friendly_name/ip/mac/vendor) are
    /// context-derivation finals on proj_devices — the query's DRIVING table — with matching
    /// expression indexes (migration 0106), so those sorts are index-driven; os/status/last_seen
    /// sort the filtered set — rare sorts, trivial at current scale. Sourced from the generated
    /// [SortableBy] allowlist so the UI cannot drift from the validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListDeviceReportAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/devices", ListDevices)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static async Task<IResult> ListDevices(
        NpgsqlDataSource db,
        string? after,
        string? status,
        string? source,
        string? os,
        string? vendor,
        string? q,
        string? sort,
        string? dir,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        string? afterSortKey = null;
        string? afterDeviceId = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecode(after, out string sortKey, out string deviceId))
            {
                return ApiError.InvalidCursor();
            }

            afterSortKey = sortKey;
            afterDeviceId = deviceId;
        }

        (List<DeviceReportItem> items, string? nextCursor) = await QueryAsync(
            db,
            status,
            source,
            os,
            vendor,
            q,
            afterSortKey,
            afterDeviceId,
            limit,
            ct,
            sort,
            dir
        );

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    /// <summary>
    /// Shared query used by both the JSON endpoint and the Devices page model.
    /// Fetches limit+1 rows to determine whether a next page exists. The sort column is
    /// resolved inside the generated command text (default ip); the keyset comparison
    /// and ORDER BY use that expression with device_id as a stable, unique tiebreaker so pages
    /// never skip or repeat rows. Descending flips both the tuple comparison and ORDER BY.
    /// </summary>
    public static async Task<(List<DeviceReportItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? status,
        string? source,
        string? os,
        string? vendor,
        string? search,
        string? afterSortKey,
        string? afterDeviceId,
        int limit,
        CancellationToken ct,
        string? sort = null,
        string? dir = null
    )
    {
        List<DeviceReportItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        // DeviceId/ManagementStatus are declared nullable only because visible_devices is a view
        // and view columns never report NOT NULL — both come from devices' NOT NULL columns and
        // can't actually be null.
        await foreach ((Guid? deviceId, string? hostname, string? friendlyName, string? ip, string? mac,
            string? obscuredMac, string? rowVendor, string? oui, string? ouiCountry, string? osFamily,
            string? osDistro, string? managementStatus, string? sources, DateTimeOffset? lastSeen, string? sortKey)
            in conn.ListDeviceReportAsync(
                StatusFilter.NormalizeStatus(status),
                Blank(source),
                Blank(os),
                Blank(vendor),
                Blank(search),
                afterSortKey,
                afterDeviceId,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new DeviceReportItem(
                    DeviceId: (deviceId ?? Guid.Empty).ToString(),
                    Hostname: hostname,
                    FriendlyName: friendlyName,
                    Ip: ip,
                    Mac: mac,
                    ObscuredMac: obscuredMac,
                    Vendor: rowVendor,
                    Oui: oui,
                    OuiCountry: ouiCountry,
                    OsFamily: osFamily,
                    OsDistro: osDistro,
                    ManagementStatus: managementStatus ?? string.Empty,
                    Sources: SplitSources(sources),
                    LastSeen: lastSeen?.UtcDateTime
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
        }

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            sortKeys.RemoveAt(sortKeys.Count - 1);
            nextCursor = KeysetCursor.Encode(sortKeys[^1], items[^1].DeviceId);
        }

        return (items, nextCursor);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] SplitSources(string? sources) =>
        string.IsNullOrEmpty(sources)
            ? Array.Empty<string>()
            : sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record DeviceReportItem(
    string DeviceId,
    string? Hostname,
    string? FriendlyName,
    string? Ip,
    string? Mac,
    string? ObscuredMac,
    string? Vendor,
    string? Oui,
    string? OuiCountry,
    string? OsFamily,
    string? OsDistro,
    string ManagementStatus,
    IReadOnlyList<string> Sources,
    DateTime? LastSeen
);