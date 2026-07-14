using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class StorageApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/storage/disks", ListDisks)
            .RequireAuthorization(ReadPolicy.Name);

        app.MapGet("/report/storage/filesystems", ListFilesystems)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListDisks(
        NpgsqlDataSource db,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<DiskListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryDisksAsync(db, parts?[0], parts?[1], lim, ct)
        );

    private static Task<IResult> ListFilesystems(
        NpgsqlDataSource db,
        string? after,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<FilesystemListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryFilesystemsAsync(db, parts?[0], parts?[1], lim, ct)
        );

    public static async Task<(List<DiskListItem> Items, string? NextCursor)> QueryDisksAsync(
        NpgsqlDataSource db,
        string? afterDevice,
        string? afterDisk,
        int limit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Device, string? Hostname, string Disk, string? Name, string? Model, string? Type, string?
            SmartHealth, double? SmartTempC, double? SmartWearPct, long? SmartPowerOnHours, long? SizeBytes)> rows =
            await conn.ListStorageDisksAsync(afterDevice, afterDisk, limit + 1, ct)
                .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(rows, limit, r => [r.Device, r.Disk]);

        List<DiskListItem> items = rows.Select(r => new DiskListItem(
                    Device: r.Device,
                    Hostname: r.Hostname,
                    Disk: r.Disk,
                    Name: r.Name,
                    Model: r.Model,
                    Type: r.Type,
                    SmartHealth: r.SmartHealth,
                    SmartTempC: r.SmartTempC,
                    SmartWearPct: r.SmartWearPct,
                    SmartPowerOnHours: r.SmartPowerOnHours,
                    SizeBytes: r.SizeBytes
                )
            )
            .ToList();

        return (items, nextCursor);
    }

    public static async Task<(List<FilesystemListItem> Items, string? NextCursor)> QueryFilesystemsAsync(
        NpgsqlDataSource db,
        string? afterDevice,
        string? afterFilesystem,
        int limit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Device, string? Hostname, string Filesystem, string? FsType, long? TotalBytes, long? UsedBytes,
            long? FreeBytes, double? UsedPct)> rows = await conn
            .ListStorageFilesystemsAsync(afterDevice, afterFilesystem, limit + 1, ct)
            .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(rows, limit, r => [r.Device, r.Filesystem]);

        List<FilesystemListItem> items = rows.Select(r => new FilesystemListItem(
                    Device: r.Device,
                    Hostname: r.Hostname,
                    Filesystem: r.Filesystem,
                    FsType: r.FsType,
                    TotalBytes: r.TotalBytes,
                    UsedBytes: r.UsedBytes,
                    FreeBytes: r.FreeBytes,
                    UsedPct: r.UsedPct
                )
            )
            .ToList();

        return (items, nextCursor);
    }
}

public sealed record DiskListItem(
    string Device,
    string? Hostname,
    string Disk,
    string? Name,
    string? Model,
    string? Type,
    string? SmartHealth,
    double? SmartTempC,
    double? SmartWearPct,
    long? SmartPowerOnHours,
    long? SizeBytes
);

public sealed record FilesystemListItem(
    string Device,
    string? Hostname,
    string Filesystem,
    string? FsType,
    long? TotalBytes,
    long? UsedBytes,
    long? FreeBytes,
    double? UsedPct
);