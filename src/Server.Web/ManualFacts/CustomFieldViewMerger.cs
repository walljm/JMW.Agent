using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.FactViews;

namespace JMW.Discovery.Server.ManualFacts;

/// <summary>
/// Merges runtime custom_field_definitions rows into the compiled <see cref="FactViewLibrary" />
/// so <see cref="FactViewRenderer" /> can render them without any renderer changes beyond
/// <see cref="FactViewColumn" />'s <c>KeyFilter</c> (docs/plans/user-provided.md, "Defining a new
/// custom field"). A definition with no <see cref="CustomFieldDefinition.TargetViewTitle" /> needs
/// no merging at all — <see cref="FactViewLibrary.CustomFieldsViewTitle" />'s baseline List view
/// already matches every <see cref="FactPaths.CustomFieldValue" /> fact generically by its raw
/// slug. Call <see cref="FilterBaselineRows" /> after rendering to keep an explicitly-targeted
/// field from also showing up a second time in that generic baseline table.
/// </summary>
public static class CustomFieldViewMerger
{
    /// <summary>
    /// Builds the view list to render: <paramref name="compiled" /> with an extra column spliced
    /// into an existing Properties view per definition that targets one, plus one synthesized
    /// Properties view per distinct new-view title. Definitions with no target, an unknown target
    /// title, or a target that isn't a Properties view are left for the baseline bucket.
    /// </summary>
    public static IReadOnlyList<FactViewDef> MergeDefs(
        IReadOnlyList<FactViewDef> compiled,
        IReadOnlyList<CustomFieldDefinition> defs
    )
    {
        List<FactViewDef> merged = [.. compiled];
        Dictionary<string, List<FactViewColumn>> newViewColumns = new(StringComparer.Ordinal);
        Dictionary<string, FactViewGroup> newViewGroups = new(StringComparer.Ordinal);

        foreach (CustomFieldDefinition def in defs)
        {
            if (def.TargetViewTitle is not { Length: > 0 } title)
            {
                continue; // baseline bucket — nothing to merge
            }

            FactViewColumn column = FactViewColumn.Fact(def.Label, FactPaths.CustomFieldValue, def.Slug);

            if (def.IsNewView)
            {
                if (!newViewColumns.TryGetValue(title, out List<FactViewColumn>? cols))
                {
                    cols = [];
                    newViewColumns[title] = cols;
                    newViewGroups[title] = ParseGroup(def.TargetViewGroup);
                }

                cols.Add(column);
                continue;
            }

            int idx = merged.FindIndex(v => v.Kind == FactViewKind.Properties
             && string.Equals(v.Title, title, StringComparison.Ordinal)
            );
            if (idx < 0)
            {
                continue; // target no longer exists / not a Properties view — falls back to baseline
            }

            merged[idx] = merged[idx] with { Columns = [.. merged[idx].Columns, column] };
        }

        foreach ((string title, List<FactViewColumn> cols) in newViewColumns)
        {
            merged.Add(new FactViewDef(title, cols, FactViewKind.Properties, newViewGroups[title]));
        }

        return merged;
    }

    /// <summary>
    /// Drops rows from the rendered baseline "Custom Fields" view whose slug is explicitly
    /// targeted elsewhere (already rendered by <see cref="MergeDefs" /> into another view), so an
    /// operator doesn't see the same value twice. Removes the view entirely if nothing is left.
    /// </summary>
    public static IReadOnlyList<RenderedFactView> FilterBaselineRows(
        IReadOnlyList<RenderedFactView> rendered,
        IReadOnlyList<CustomFieldDefinition> defs
    )
    {
        HashSet<string> targetedSlugs = defs
            .Where(d => d.TargetViewTitle is { Length: > 0 })
            .Select(d => d.Slug)
            .ToHashSet(StringComparer.Ordinal);

        if (targetedSlugs.Count == 0)
        {
            return rendered;
        }

        List<RenderedFactView> result = new(rendered.Count);
        foreach (RenderedFactView view in rendered)
        {
            if (!string.Equals(view.Title, FactViewLibrary.CustomFieldsViewTitle, StringComparison.Ordinal))
            {
                result.Add(view);
                continue;
            }

            List<IReadOnlyList<string?>> keptRows = view.Rows
                .Where(row => row.Count == 0 || row[0] is not { } slug || !targetedSlugs.Contains(slug))
                .ToList();

            if (keptRows.Count > 0)
            {
                result.Add(view with { Rows = keptRows });
            }
        }

        return result;
    }

    private static FactViewGroup ParseGroup(string? group) =>
        Enum.TryParse(group, ignoreCase: true, out FactViewGroup parsed) ? parsed : FactViewGroup.Custom;
}