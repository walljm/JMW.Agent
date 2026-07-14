using System.Text.Json;

using JMW.Discovery.Core;

namespace JMW.Discovery.Server.FactViews;

/// <summary>
/// One device fact the renderer consumes: the templated path, the row-identity key
/// (non-Device dimension values), and the value. Adapted from the All-Facts query result.
/// </summary>
public sealed record FactViewFact(string AttributePath, string Key, string? Value);

/// <summary>
/// A rendered fact-view table: title, column headers, and rows of cell values.
/// Carries the source view's <see cref="FactViewGroup" /> and <see cref="FactViewKind" /> so the
/// device-detail section nav can file it under the right group and decide whether a row-count
/// chip is meaningful (List views count rows; Properties sheets do not).
/// </summary>
public sealed record RenderedFactView(
    string Title,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    FactViewGroup Group,
    FactViewKind Kind
);

/// <summary>
/// Turns a device's flat fact list into the display tables declared in the fact-view library.
/// A fact belongs to a view when its (DimKey, Attribute) — derived by the same
/// <see cref="Fact" /> grammar projections route on — matches one of the view's attribute
/// columns. Matching facts are grouped by their row-identity key and pivoted into columns.
/// Pure and DB-free: the page fetches facts once (for All Facts) and passes them here.
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
        IReadOnlyList<FactViewDef> views
    )
    {
        List<RenderedFactView> result = new(views.Count);

        foreach (FactViewDef view in views)
        {
            RenderedFactView? rendered = view.Kind == FactViewKind.Properties
                ? RenderProperties(facts, view)
                : RenderList(facts, view);
            if (rendered is not null)
            {
                result.Add(rendered);
            }
        }

        return result;
    }

    // A scalar property sheet: each column is one device fact → a (Property, Value) row.
    // Rows with no fact are omitted so the sheet shows only what's known.
    private static RenderedFactView? RenderProperties(IReadOnlyList<FactViewFact> facts, FactViewDef view)
    {
        List<IReadOnlyList<string?>> rows = new();
        foreach (FactViewColumn col in view.Columns)
        {
            if (col.FactPath is null)
            {
                continue;
            }

            string dimKey = Fact.DeriveDimKey(col.FactPath);
            string attr = Fact.DeriveAttribute(col.FactPath);
            string? value = facts
                .FirstOrDefault(f => Fact.DeriveDimKey(f.AttributePath) == dimKey
                 && Fact.DeriveAttribute(f.AttributePath) == attr
                 && (col.KeyFilter is null || string.Equals(f.Key, col.KeyFilter, StringComparison.Ordinal))
                )
                ?.Value;
            if (value is not null)
            {
                rows.Add([col.Label, value]);
            }
        }

        return rows.Count == 0
            ? null
            : new RenderedFactView(view.Title, ["Property", "Value"], rows, view.Group, view.Kind);
    }

    private static RenderedFactView? RenderList(IReadOnlyList<FactViewFact> facts, FactViewDef view)
    {
        {
            // The view's dimension comes from its first attribute column; all attribute
            // columns of a view share it. A key-only view has nothing to match on.
            string? sample = view.Columns.FirstOrDefault(c => c.FactPath is not null)?.FactPath;
            if (sample is null)
            {
                return null;
            }

            string dimKey = Fact.DeriveDimKey(sample);
            HashSet<string> wantedAttrs = view.Columns
                .Where(c => c.FactPath is not null)
                .Select(c => Fact.DeriveAttribute(c.FactPath!))
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

            List<IReadOnlyList<string?>> rows = new();
            foreach (IGrouping<string, FactViewFact> group in matching
                .GroupBy(f => f.Key, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<string?> row = new(view.Columns.Count);
                foreach (FactViewColumn col in view.Columns)
                {
                    if (col.FactPath is null)
                    {
                        row.Add(group.Key); // the row's dimension-key column
                    }
                    else
                    {
                        string attr = Fact.DeriveAttribute(col.FactPath);
                        row.Add(group.FirstOrDefault(f => Fact.DeriveAttribute(f.AttributePath) == attr)?.Value);
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
    }
}