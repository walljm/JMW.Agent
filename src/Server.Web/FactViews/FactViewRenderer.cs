using System.Globalization;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Server.UI;

namespace JMW.Discovery.Server.FactViews;

/// <summary>
/// One device fact the renderer consumes: the templated path, the row-identity key
/// (non-Device dimension values), and the value. Adapted from the All-Facts query result.
/// </summary>
public sealed record FactViewFact(string AttributePath, string Key, string? Value);

/// <summary>
/// One rendered table cell: the human <see cref="Display" /> string and an optional
/// <see cref="SortValue" /> the client-side sortable table sorts on instead of the display text
/// (e.g. the raw byte count behind "1.5 GB", so numeric columns order numerically). When
/// <see cref="SortValue" /> is null the client sorts on <see cref="Display" />.
/// </summary>
public sealed record FactCell(string? Display, string? SortValue = null);

/// <summary>
/// Render-time services a fact view may need that aren't in the device's own facts. Kept off
/// <see cref="FactViewRenderer" /> itself so the renderer stays pure and DB-free (tests pass a
/// fake resolver). Today: the OUI resolver — MAC → (vendor, country) — since OUI lives in a
/// Postgres function, not the fact stream; the page resolves the device's MACs once and injects
/// the map here.
/// </summary>
public sealed record FactViewRenderContext(
    Func<string?, (string? Vendor, string? Country)>? OuiResolver = null
)
{
    /// <summary>No external services — every <see cref="FactViewFormat.Oui" /> cell renders as unknown.</summary>
    public static readonly FactViewRenderContext Empty = new();
}

/// <summary>
/// A rendered fact-view table: title, column headers, and rows of cells.
/// Carries the source view's <see cref="FactViewGroup" /> and <see cref="FactViewKind" /> so the
/// device-detail section nav can file it under the right group and decide whether a row-count
/// chip is meaningful (List views count rows; Properties sheets do not).
/// </summary>
public sealed record RenderedFactView(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<FactCell>> Rows,
    FactViewGroup Group,
    FactViewKind Kind
);

/// <summary>
/// Turns a device's flat fact list into the display tables declared in the fact-view library.
/// A fact belongs to a view when its (DimKey, Attribute) — derived by the same
/// <see cref="Fact" /> grammar projections route on — matches one of the view's attribute
/// columns. Matching facts are grouped by their row-identity key and pivoted into columns.
/// Pure and DB-free: the page fetches facts once (for All Facts) and passes them here, along with
/// an optional <see cref="FactViewRenderContext" /> for lookups the facts don't carry (OUI).
/// </summary>
public static class FactViewRenderer
{
    /// <summary>
    /// Renders the list-dimension keys from a fact's key_values JSON (all but
    /// <paramref name="excludeDimension" />, the outer entity dimension — "Device" or "Service")
    /// as a compact display string, e.g. {"Device":..,"Discovered":"192.168.1.219"} →
    /// "192.168.1.219". Empty when the fact has no other list dimension.
    /// </summary>
    public static string ExtractRowKey(string? keyValuesJson, string excludeDimension)
    {
        if (string.IsNullOrEmpty(keyValuesJson))
        {
            return "";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(keyValuesJson);
            List<string> parts = [];
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, excludeDimension, StringComparison.Ordinal))
                {
                    parts.Add(p.Value.ToString());
                }
            }

            return string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static IReadOnlyList<RenderedFactView> Render(
        IReadOnlyList<FactViewFact> facts,
        IReadOnlyList<FactViewDef> views,
        FactViewRenderContext? context = null
    )
    {
        FactViewRenderContext ctx = context ?? FactViewRenderContext.Empty;
        List<RenderedFactView> result = new(views.Count);

        foreach (FactViewDef view in views)
        {
            RenderedFactView? rendered = view.Kind == FactViewKind.Properties
                ? RenderProperties(facts, view, ctx)
                : RenderList(facts, view, ctx);
            if (rendered is not null)
            {
                result.Add(rendered);
            }
        }

        return result;
    }

    // A scalar property sheet: each column is one device fact → a (Property, Value) row.
    // Rows with no fact (or a computed column that yields nothing) are omitted so the sheet shows
    // only what's known.
    private static RenderedFactView? RenderProperties(
        IReadOnlyList<FactViewFact> facts,
        FactViewDef view,
        FactViewRenderContext ctx
    )
    {
        // Resolve every path a column needs — its own FactPath (honoring KeyFilter), plus a
        // computed column's declared DependsOn — so computed columns can read them back out by
        // FactPath.
        Dictionary<string, string?> sheetFacts = new(StringComparer.Ordinal);
        foreach (FactViewColumn col in view.Columns)
        {
            if (col.FactPath is not null)
            {
                ResolveInto(sheetFacts, facts, col.FactPath, col.KeyFilter);
            }

            foreach (string path in col.DependsOn)
            {
                ResolveInto(sheetFacts, facts, path, keyFilter: null);
            }
        }

        List<IReadOnlyList<FactCell>> rows = new();
        foreach (FactViewColumn col in view.Columns)
        {
            string? raw = col.Compute is not null
                ? col.Compute(sheetFacts)
                : col.FactPath is not null && sheetFacts.TryGetValue(col.FactPath, out string? v) ? v : null;
            if (raw is null)
            {
                continue;
            }

            rows.Add([new FactCell(col.Label), FormatCell(raw, col.Format, ctx)]);
        }

        return rows.Count == 0
            ? null
            : new RenderedFactView(view.Title, ["Property", "Value"], rows, view.Group, view.Kind);
    }

    private static RenderedFactView? RenderList(
        IReadOnlyList<FactViewFact> facts,
        FactViewDef view,
        FactViewRenderContext ctx
    )
    {
        // The view's dimension comes from its first attribute (or computed-dependency) path; all
        // paths in a view — declared or depended-on — must share it, same as every other attribute
        // column. A key-only view has nothing to match on.
        string? sample = view.Columns
            .Select(c => c.FactPath ?? (c.DependsOn.Count > 0 ? c.DependsOn[0] : null))
            .FirstOrDefault(p => p is not null);
        if (sample is null)
        {
            return null;
        }

        string dimKey = Fact.DeriveDimKey(sample);
        HashSet<string> wantedAttrs = view.Columns
            .SelectMany(c => c.FactPath is not null ? [c.FactPath] : c.DependsOn)
            .Select(Fact.DeriveAttribute)
            .ToHashSet(StringComparer.Ordinal);

        List<FactViewFact> matching = facts
            .Where(f => Fact.DeriveDimKey(f.AttributePath) == dimKey
             && wantedAttrs.Contains(Fact.DeriveAttribute(f.AttributePath))
            )
            .ToList();

        if (matching.Count == 0)
        {
            return null; // nothing to show — omit the table entirely
        }

        List<IReadOnlyList<FactCell>> rows = new();
        foreach (IGrouping<string, FactViewFact> group in matching
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // The row's facts, keyed by full templated path, so a computed column can read any
            // of them via its FactPaths constant.
            Dictionary<string, string?> rowFacts = new(StringComparer.Ordinal);
            foreach (FactViewFact f in group)
            {
                rowFacts[f.AttributePath] = f.Value;
            }

            List<FactCell> row = new(view.Columns.Count);
            foreach (FactViewColumn col in view.Columns)
            {
                if (col.Compute is not null)
                {
                    row.Add(FormatCell(col.Compute(rowFacts), col.Format, ctx));
                }
                else if (col.FactPath is null)
                {
                    row.Add(new FactCell(group.Key)); // the row's dimension-key column
                }
                else
                {
                    string attr = Fact.DeriveAttribute(col.FactPath);
                    string? raw = group.FirstOrDefault(f => Fact.DeriveAttribute(f.AttributePath) == attr)?.Value;
                    row.Add(FormatCell(raw, col.Format, ctx));
                }
            }

            rows.Add(row);
        }

        return new RenderedFactView(
            view.Title,
            view.Columns.Select(c => c.Label).ToList(),
            rows,
            view.Group,
            view.Kind
        );
    }

    /// <summary>
    /// Turns a raw stored value into a display cell per <paramref name="format" />, carrying a raw
    /// numeric sort value for the humanized numeric formats so client-side sorting stays numeric.
    /// Unparseable values fall back to the raw string (never throws on bad data).
    /// </summary>
    internal static FactCell FormatCell(string? raw, FactViewFormat format, FactViewRenderContext ctx)
    {
        if (raw is null)
        {
            return new FactCell(null);
        }

        switch (format)
        {
            case FactViewFormat.Bytes:
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                    ? new FactCell(ViewFormat.Bytes(bytes), raw)
                    : new FactCell(raw);

            case FactViewFormat.BytesPerSecond:
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bps)
                    ? new FactCell(ViewFormat.Bytes(bps) + "/s", raw)
                    : new FactCell(raw);

            case FactViewFormat.Percent:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)
                    ? new FactCell(pct.ToString("0.#", CultureInfo.InvariantCulture) + "%", raw)
                    : new FactCell(raw);

            case FactViewFormat.Celsius:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double temp)
                    ? new FactCell(temp.ToString("0.#", CultureInfo.InvariantCulture) + " °C", raw)
                    : new FactCell(raw);

            case FactViewFormat.UnixSeconds:
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch)
                    ? new FactCell(
                        DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                            .ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        raw)
                    : new FactCell(raw);

            case FactViewFormat.DurationSeconds:
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long secs)
                    ? new FactCell(ViewFormat.Duration(secs), raw)
                    : new FactCell(raw);

            case FactViewFormat.Bool:
                return new FactCell(IsTruthy(raw) ? "Yes" : "No");

            case FactViewFormat.Oui:
                (string? vendor, string? country) = ctx.OuiResolver?.Invoke(raw) ?? (null, null);
                return new FactCell(ViewFormat.FormatOui(vendor, country));

            default:
                return new FactCell(raw);
        }
    }

    private static bool IsTruthy(string raw) =>
        raw.Equals("true", StringComparison.OrdinalIgnoreCase)
        || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || raw == "1";

    /// <summary>Looks up one fact path's value on a Properties sheet (optionally scoped to a
    /// specific list-dimension instance via <paramref name="keyFilter" />) and stores it into
    /// <paramref name="sheetFacts" /> if present.</summary>
    private static void ResolveInto(
        Dictionary<string, string?> sheetFacts,
        IReadOnlyList<FactViewFact> facts,
        string path,
        string? keyFilter
    )
    {
        string dimKey = Fact.DeriveDimKey(path);
        string attr = Fact.DeriveAttribute(path);
        string? value = facts
            .FirstOrDefault(f => Fact.DeriveDimKey(f.AttributePath) == dimKey
             && Fact.DeriveAttribute(f.AttributePath) == attr
             && (keyFilter is null || string.Equals(f.Key, keyFilter, StringComparison.Ordinal))
            )
            ?.Value;
        if (value is not null)
        {
            sheetFacts[path] = value;
        }
    }
}