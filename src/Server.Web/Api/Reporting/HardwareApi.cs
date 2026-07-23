using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

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

    /// <summary>Must match the first [SortableBy] key on ReportingQueries.ListHardwareAsync —
    /// the generated command text falls back to that column for an unrecognized sort.</summary>
    public const string DefaultSort = "hostname";

    /// <summary>
    /// Columns the hardware list may be sorted by — hostname rides proj_devices' resolved
    /// identity column, cpu the driving proj_hardware table (0104 index); vendor is a
    /// cross-table COALESCE (deliberate DMI-first display priority), a rare unindexable sort.
    /// Sourced from the generated [SortableBy] allowlist so the UI cannot drift from the
    /// validated SQL variants.
    /// </summary>
    public static readonly IReadOnlySet<string> SortableColumns = ReportingQueries.ListHardwareAsyncSortKeys;

    public static bool IsSortable(string? sort) => sort is not null && SortableColumns.Contains(sort);

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
        List<HardwareListItem> items = new();
        // sortKeys[i] is the SQL-computed sort expression for items[i] — used verbatim for the
        // cursor so it always matches the keyset comparison (no C#-side re-derivation to drift).
        List<string> sortKeys = new();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        await foreach ((string device, string? hostname, string? systemVendor, string? systemModel,
            string? systemSerial, string? biosVersion, string? virtualization, string? cpuModel, string? cpuVendor,
            long? cpuCores, long? cpuLogicalCores, double? cpuMhz, long? totalMemBytes, long? totalStorageBytes,
            string? sortKey, string? friendlyName)
            in conn.ListHardwareAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                afterSortKey,
                afterDevice,
                limit + 1,
                sort,
                dir,
                ct
            ))
        {
            items.Add(
                new HardwareListItem(
                    Device: device,
                    Hostname: hostname,
                    SystemVendor: systemVendor,
                    SystemModel: systemModel,
                    SystemSerial: systemSerial,
                    BiosVersion: biosVersion,
                    Virtualization: virtualization,
                    CpuModel: cpuModel,
                    CpuVendor: cpuVendor,
                    CpuCores: cpuCores,
                    CpuLogicalCores: cpuLogicalCores,
                    CpuMhz: cpuMhz,
                    TotalMemBytes: totalMemBytes,
                    TotalStorageBytes: totalStorageBytes,
                    FriendlyName: friendlyName
                )
            );
            sortKeys.Add(sortKey ?? string.Empty);
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