using System.Globalization;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ChangesModel : ReportPageModel
{
    private readonly ILogger<ChangesModel> _logger;
    private readonly NpgsqlDataSource _db;

    public ChangesModel(NpgsqlDataSource db, ILogger<ChangesModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IReadOnlyList<ChangeListItem> Changes { get; private set; } = [];
    public string? NextCursor { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Since { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? DeviceId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        DateTimeOffset? afterCollectedAt = null;
        string? afterId = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 2, out string[] parts)
         && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            afterCollectedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            afterId = parts[1];
        }

        return await RunReportLoadAsync(
            async () =>
            {
                (IReadOnlyList<ChangeListItem> items, string? next) = await ChangesApi.QueryAsync(
                    _db,
                    Since,
                    DeviceId,
                    afterCollectedAt,
                    afterId,
                    ChangesApi.DefaultLimit,
                    ct
                );
                Changes = items;
                NextCursor = next;
            },
            ex => ChangesModelLog.LoadFailed(_logger, ex),
            "_ChangesTable"
        );
    }
}

internal static partial class ChangesModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Changes page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}