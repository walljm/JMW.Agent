using System.Globalization;
using System.Text.Json;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;
using JMW.Discovery.Server.UI;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Admin;

[Authorize(Policy = RbacPolicies.Admin)]
public sealed class AuditLogModel : PageModel
{
    private readonly NpgsqlDataSource _db;

    public AuditLogModel(NpgsqlDataSource db)
    {
        _db = db;
    }

    private const int PageSize = 50;

    public List<AuditEntry> Entries { get; private set; } = [];
    public string? NextCursor { get; private set; }
    public DataGridModel FilterBar { get; private set; } = null!;
    public PaginationLinks Pagination { get; private set; } = null!;
    public string ClearDatesHref { get; private set; } = "/admin/audit-log";

    [BindProperty(SupportsGet = true)]
    public string? After { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Action { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Actor { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Since { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Until { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        DateTimeOffset? afterOccurredAt = null;
        long? afterId = null;
        if (!string.IsNullOrEmpty(After) && KeysetCursor.TryDecodeParts(After, 2, out string[] parts))
        {
            if (DateTimeOffset.TryParse(parts[0], out DateTimeOffset ts))
            {
                afterOccurredAt = ts;
            }

            if (long.TryParse(parts[1], out long parsedId))
            {
                afterId = parsedId;
            }
        }

        DateTimeOffset? sinceUtc = DateTimeOffset.TryParse(Since, out DateTimeOffset since) ? since : null;
        DateTimeOffset? untilUtc = DateTimeOffset.TryParse(Until, out DateTimeOffset until)
            ? until.Date.AddDays(1).AddTicks(-1)
            : null;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<string> knownActions = await ListDistinctAsync(conn, "action", ct);
        List<string> knownActors = await ListDistinctAsync(conn, "actor", ct);

        await foreach ((long Id, DateTimeOffset OccurredAt, string ActorValue, string ActionValue, string? TargetRef,
            JsonElement? Detail) row in
            conn.ListAuditLogAsync(
                PageSize + 1,
                afterOccurredAt,
                afterId,
                string.IsNullOrEmpty(Action) ? null : Action,
                string.IsNullOrEmpty(Actor) ? null : Actor,
                string.IsNullOrEmpty(Q) ? null : Q,
                sinceUtc,
                untilUtc,
                ct
            ))
        {
            Entries.Add(
                new AuditEntry(
                    row.Id,
                    row.OccurredAt.UtcDateTime,
                    row.ActorValue,
                    row.ActionValue,
                    row.TargetRef,
                    row.Detail.HasValue ? JsonSerializer.Serialize(row.Detail.Value, PrettyJson) : null,
                    AuditEntity.Classify(row.ActionValue),
                    AuditSeverity.Classify(row.ActionValue)
                )
            );
        }

        if (Entries.Count > PageSize)
        {
            Entries.RemoveAt(Entries.Count - 1);
            AuditEntry last = Entries[^1];
            NextCursor = KeysetCursor.EncodeParts(
                last.OccurredAt.ToString("O"),
                last.Id.ToString(CultureInfo.InvariantCulture)
            );
        }

        await ResolveEntityLinksAsync(conn, ct);

        Dictionary<string, string> activeFilters = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(Action))
        {
            activeFilters["action"] = Action;
        }

        if (!string.IsNullOrEmpty(Actor))
        {
            activeFilters["actor"] = Actor;
        }

        List<string> clearDatesParts = [];
        if (!string.IsNullOrEmpty(Action))
        {
            clearDatesParts.Add("action=" + Uri.EscapeDataString(Action));
        }

        if (!string.IsNullOrEmpty(Actor))
        {
            clearDatesParts.Add("actor=" + Uri.EscapeDataString(Actor));
        }

        if (!string.IsNullOrEmpty(Q))
        {
            clearDatesParts.Add("q=" + Uri.EscapeDataString(Q));
        }

        ClearDatesHref = clearDatesParts.Count == 0
            ? "/admin/audit-log"
            : "/admin/audit-log?" + string.Join('&', clearDatesParts);

        GridModel grid = GridModelBuilder.Build(
            "/admin/audit-log",
            [
                new FilterSpec("action", "Action", knownActions.Select(a => new FilterValue(a, a)).ToList()),
                new FilterSpec("actor", "Actor", knownActors.Select(a => new FilterValue(a, a)).ToList()),
            ],
            activeFilters,
            Q,
            "occurred_at",
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            After,
            NextCursor,
            "#audit-log-table"
        );
        FilterBar = grid.FilterBar;
        Pagination = grid.Pagination;

        // The FilterBar's htmx chip clicks only append their own action/actor/q onto
        // FragmentUrl (see filter-bar.js _swap) — bake the date range in here so a chip
        // change doesn't silently drop the currently active Since/Until.
        if (!string.IsNullOrEmpty(Since) || !string.IsNullOrEmpty(Until))
        {
            string dateSuffix = (string.IsNullOrEmpty(Since) ? "" : "&since=" + Uri.EscapeDataString(Since))
                + (string.IsNullOrEmpty(Until) ? "" : "&until=" + Uri.EscapeDataString(Until));
            FilterBar = new DataGridModel
            {
                Filters = FilterBar.Filters,
                ActiveFilters = FilterBar.ActiveFilters,
                Q = FilterBar.Q,
                NextCursor = FilterBar.NextCursor,
                FragmentUrl = FilterBar.FragmentUrl + dateSuffix,
                HtmxTarget = FilterBar.HtmxTarget,
                PageUrl = FilterBar.PageUrl,
            };
        }

        if (string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial("_AuditLogTable", this);
        }

        return Page();
    }

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static async Task<List<string>> ListDistinctAsync(NpgsqlConnection conn, string column, CancellationToken ct)
    {
        List<string> values = [];
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT {column} FROM audit_log ORDER BY {column}";
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Fills in <see cref="AuditEntry.LinkHref" /> for rows whose target is directly addressable
    /// (agent/device ids), and batch-resolves the owning agent for target rows — one lookup for
    /// the whole page, not per row. Rows for a target that has since been deleted resolve to no
    /// link; the owning agent is gone with it.
    /// </summary>
    private async Task ResolveEntityLinksAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            AuditEntry e = Entries[i];
            string? href = e.Entity switch
            {
                "agent" => e.TargetRef is not null ? "/fleet/agents/" + e.TargetRef : null,
                "device" => e.TargetRef is not null ? "/devices/" + e.TargetRef : null,
                "credential" => "/admin/credentials",
                "user" => "/admin/users",
                _ => null,
            };
            if (href is not null)
            {
                Entries[i] = e with { LinkHref = href };
            }
        }

        await ResolveOwningAgentLinksAsync(conn, "target", "targets", "target_id", "targets", ct);
    }

    private async Task ResolveOwningAgentLinksAsync(
        NpgsqlConnection conn,
        string entity,
        string table,
        string idColumn,
        string tabName,
        CancellationToken ct
    )
    {
        string?[] ids = Entries.Where(e => e.Entity == entity && e.TargetRef is not null)
            .Select(e => e.TargetRef)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        Dictionary<string, Guid> ownerByRef = new(StringComparer.Ordinal);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {idColumn}, agent_id FROM {table} WHERE {idColumn}::text = ANY($1)";
        cmd.Parameters.Add(Param.TextArray(ids));
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ownerByRef[reader.GetGuid(0).ToString()] = reader.GetGuid(1);
        }

        for (int i = 0; i < Entries.Count; i++)
        {
            AuditEntry e = Entries[i];
            if (e.Entity == entity && e.TargetRef is not null && ownerByRef.TryGetValue(e.TargetRef, out Guid agentId))
            {
                Entries[i] = e with { LinkHref = "/fleet/agents/" + agentId + "?tab=" + tabName };
            }
        }
    }
}

/// <summary>Which kind of entity an audit row's <c>target_ref</c> points at, driving link
/// resolution. Derived from the action string rather than stored, since the write side never
/// records it explicitly.</summary>
public static class AuditEntity
{
    public static string? Classify(string action) =>
        action switch
        {
            "login.oidc" or "oidc.user_provisioned" or "password_change.success" => "user",
            "login.failure" or "password_change.failure" or "bootstrap.admin_created" => null,
            _ when action.StartsWith("agent.", StringComparison.Ordinal) => "agent",
            _ when action.StartsWith("device.", StringComparison.Ordinal) => "device",
            // "service_target." is the pre-unification action prefix (ServiceTargetsApi,
            // now merged into TargetsApi) — historical rows still carry it, and their
            // target_ref values were preserved as target_id in the unified targets table
            // during the merge migration, so they resolve the same way "target." rows do.
            _ when action.StartsWith("target.", StringComparison.Ordinal) => "target",
            _ when action.StartsWith("service_target.", StringComparison.Ordinal) => "target",
            _ when action.StartsWith("credential.", StringComparison.Ordinal) => "credential",
            _ when action.StartsWith("user.", StringComparison.Ordinal) => "user",
            _ => null,
        };
}

/// <summary>Maps an audit action to a `.status` severity class — reuses the same ok/warn/crit
/// vocabulary as every other status pill in the app rather than inventing a new one.</summary>
public static class AuditSeverity
{
    public static string Classify(string action)
    {
        if (action is "login.failure" or "password_change.failure")
        {
            return "warn";
        }

        string verb = action[(action.LastIndexOf('.') + 1)..];
        return verb switch
        {
            "delete" => "crit",
            "disable" or "toggle" => "warn",
            _ => "ok",
        };
    }
}

/// <summary>Turns a raw <c>entity.verb</c> action string into a readable label for display —
/// the raw string is still shown as a title tooltip so nothing technical is lost.</summary>
public static class AuditActionLabel
{
    public static string Humanize(string action) =>
        string.Join(" · ", action.Split('.').Select(HumanizeWord));

    private static string HumanizeWord(string word) =>
        string.Join(' ', word.Split('_').Select(Capitalize));

    private static string Capitalize(string token) =>
        token switch
        {
            "" => token,
            "oidc" => "OIDC",
            _ => char.ToUpperInvariant(token[0]) + token[1..],
        };
}

public sealed record AuditEntry(
    long Id,
    DateTime OccurredAt,
    string Actor,
    string Action,
    string? TargetRef,
    string? Detail,
    string? Entity,
    string Severity
)
{
    public string? LinkHref { get; init; }
}