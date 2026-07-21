using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class StorageModel : PageModel
{
    private readonly ILogger<StorageModel> _logger;
    private readonly NpgsqlDataSource _db;

    public StorageModel(NpgsqlDataSource db, ILogger<StorageModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<DiskListItem> Disks { get; private set; } = [];
    public IReadOnlyList<FilesystemListItem> Filesystems { get; private set; } = [];
    public string? DisksNextCursor { get; private set; }
    public string? FilesystemsNextCursor { get; private set; }
    public string? DisksError { get; private set; }
    public string? FilesystemsError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? AfterDisk { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? AfterFs { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    /// <summary>Normalizes the bound `?tab=` query value; anything but "filesystems" is "disks".</summary>
    public string ActiveTab => Tab == "filesystems" ? "filesystems" : "disks";

    public async Task OnGetAsync(CancellationToken ct)
    {
        string? afterDiskDevice = null;
        string? afterDiskKey = null;
        if (!string.IsNullOrEmpty(AfterDisk)
         && KeysetCursor.TryDecodeParts(AfterDisk, 2, out string[] diskParts))
        {
            afterDiskDevice = diskParts[0];
            afterDiskKey = diskParts[1];
        }

        string? afterFsDevice = null;
        string? afterFsKey = null;
        if (!string.IsNullOrEmpty(AfterFs)
         && KeysetCursor.TryDecodeParts(AfterFs, 2, out string[] fsParts))
        {
            afterFsDevice = fsParts[0];
            afterFsKey = fsParts[1];
        }

        try
        {
            (IReadOnlyList<DiskListItem> disks, string? disksNext) = await StorageApi.QueryDisksAsync(
                _db,
                Q,
                afterDiskDevice,
                afterDiskKey,
                StorageApi.DefaultLimit,
                ct
            );
            Disks = disks;
            DisksNextCursor = disksNext;
        }
        catch (NpgsqlException ex)
        {
            DisksError = ReportPageModel.SafeLoadErrorMessage;
            StorageModelLog.DisksLoadFailed(_logger, ex);
        }

        try
        {
            (IReadOnlyList<FilesystemListItem> fs, string? fsNext) = await StorageApi.QueryFilesystemsAsync(
                _db,
                Q,
                afterFsDevice,
                afterFsKey,
                StorageApi.DefaultLimit,
                ct
            );
            Filesystems = fs;
            FilesystemsNextCursor = fsNext;
        }
        catch (NpgsqlException ex)
        {
            FilesystemsError = ReportPageModel.SafeLoadErrorMessage;
            StorageModelLog.FilesystemsLoadFailed(_logger, ex);
        }
    }
}

internal static partial class StorageModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Storage disks page load failed.")]
    public static partial void DisksLoadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage filesystems page load failed.")]
    public static partial void FilesystemsLoadFailed(ILogger logger, Exception ex);
}