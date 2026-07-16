using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Extra report columns backed by operator-authored facts: any device-scoped arbitrary fact path
/// flagged with fact_path_metadata.show_in_reports appears as an additional column in reports that
/// list devices (docs/plans/architecture-operator-facts.md follow-up; migration 0084). Values are
/// read from the facts_history operator subset (source = 2, fronted by
/// facts_history_operator_path_idx) for just the page's devices — display-only, so there is no
/// projection table and the columns are never sortable/filterable.
/// </summary>
public static class OperatorFactColumns
{
    /// <summary>One flagged path. <see cref="Heading" /> is what the table header shows.</summary>
    public sealed record Column(string AttributePath, string? Label)
    {
        public string Heading =>
            !string.IsNullOrEmpty(Label)
                ? Label
                : AttributePath.StartsWith("Device[].", StringComparison.Ordinal)
                    ? AttributePath["Device[].".Length..]
                    : AttributePath;
    }

    /// <summary>
    /// Loads the flagged columns and, when any exist, the latest operator-authored value per
    /// (device, path) for the given page of devices. Returns an empty column list (and empty map)
    /// when nothing is flagged — the common case, costing one metadata-table read.
    /// </summary>
    public static async Task<(IReadOnlyList<Column> Columns, IReadOnlyDictionary<(string Device, string Path), string> Values)>
        LoadAsync(NpgsqlDataSource db, IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<Column> columns = await conn.GetReportFactColumnsAsync(ct)
            .Select(r => new Column(r.AttributePath, r.Label))
            .ToListAsync(ct);

        if (columns.Count == 0 || deviceIds.Count == 0)
        {
            return (columns, new Dictionary<(string, string), string>());
        }

        // Latest operator value per fact id, restricted to the flagged paths and the page's
        // devices. Served by facts_history_operator_path_idx (attribute_path, id WHERE source=2).
        const string sql = """
            SELECT DISTINCT ON (h.id)
                h.key_values ->> 'Device' AS device
              , h.attribute_path
              , COALESCE(h.value_str, h.value_long::text, h.value_double::text) AS value
            FROM facts_history h
            WHERE h.source = 2
              AND h.attribute_path = ANY($1)
              AND h.key_values ->> 'Device' = ANY($2)
            ORDER BY h.id, h.collected_at DESC
            """;

        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(Param.TextArray([.. columns.Select(c => c.AttributePath)]));
        cmd.Parameters.Add(Param.TextArray([.. deviceIds]));

        Dictionary<(string, string), string> values = new();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(2))
            {
                continue;
            }

            values[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return (columns, values);
    }
}
