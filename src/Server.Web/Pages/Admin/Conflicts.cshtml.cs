using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class ConflictsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;
    private readonly NpgsqlDataSource _db;

    public ConflictsModel(IAntiforgery antiforgery, NpgsqlDataSource db)
    {
        _antiforgery = antiforgery;
        _db = db;
    }

    public List<ConflictItem> Conflicts { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public string AntiforgeryToken { get; private set; } = string.Empty;
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;

    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? FpType { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;

        string? afterFpType = null;
        string? afterFpValue = null;
        if (!string.IsNullOrEmpty(After)
         && KeysetCursor.TryDecodeParts(After, 2, out string[] parts))
        {
            afterFpType = parts[0];
            afterFpValue = parts[1];
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<string> knownTypes = [];
        await using (NpgsqlCommand typesCmd = conn.CreateCommand())
        {
            typesCmd.CommandText = "SELECT DISTINCT fp_type FROM device_fingerprints ORDER BY fp_type";
            await using NpgsqlDataReader typesReader = await typesCmd.ExecuteReaderAsync(ct);
            while (await typesReader.ReadAsync(ct))
            {
                knownTypes.Add(typesReader.GetString(0));
            }
        }

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT df.fp_type, df.fp_value,
                   array_agg(DISTINCT df.device_id::text ORDER BY df.device_id::text) AS device_ids
            FROM device_fingerprints df
            WHERE NOT EXISTS (
                SELECT 1 FROM excluded_fingerprints ef
                WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
            )
            AND ($1::text IS NULL OR df.fp_type = $1)
            AND ($2::text IS NULL OR (df.fp_type, df.fp_value) > ($2::text, $3::text))
            GROUP BY df.fp_type, df.fp_value
            HAVING COUNT(DISTINCT df.device_id) > 1
            ORDER BY df.fp_type ASC, df.fp_value ASC
            LIMIT $4
            """;
        cmd.Parameters.Add(Param.Text(FpType));
        cmd.Parameters.Add(Param.Text(afterFpType));
        cmd.Parameters.Add(Param.Text(afterFpValue));
        cmd.Parameters.Add(Param.Integer(PageSize + 1));

        await using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                Conflicts.Add(
                    new ConflictItem(
                        FpType: reader.GetString(0),
                        FpValue: reader.GetString(1),
                        DeviceIds: (string[])reader.GetValue(2)
                    )
                );
            }
        }

        if (Conflicts.Count > PageSize)
        {
            Conflicts.RemoveAt(Conflicts.Count - 1);
            ConflictItem last = Conflicts[^1];
            NextCursor = KeysetCursor.EncodeParts(last.FpType, last.FpValue);
        }

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(FpType))
        {
            activeFilters["fp_type"] = FpType;
        }

        GridModel grid = GridModelBuilder.Build(
            "/admin/conflicts",
            [
                new FilterSpec(
                    "fp_type",
                    "Type",
                    knownTypes.Select(t => new FilterValue(t, t)).ToList()
                ),
            ],
            activeFilters,
            null,
            "fp_type",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#conflicts-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        if (string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial("_ConflictsTable", this);
        }

        return Page();
    }
}

public sealed record ConflictItem(string FpType, string FpValue, string[] DeviceIds);