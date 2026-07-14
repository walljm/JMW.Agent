using JMW.Discovery.Core;

namespace JMW.Discovery.Server.FactViews;

/// <summary>
/// One column of a <see cref="FactViewDef" />. A column is either the row's dimension KEY
/// (the list-element identity, e.g. the thermal zone name) or a fact ATTRIBUTE whose value
/// fills the cell. Attribute columns reference a FactPaths constant so the
/// (DimKey, Attribute) routing grammar — shared with projections via <see cref="Fact" /> —
/// is derived, never hand-typed.
/// </summary>
public sealed record FactViewColumn(string Label, string? FactPath, string? KeyFilter = null)
{
    /// <summary>A column showing the row's dimension key (the list-element identity).</summary>
    public static FactViewColumn Key(string label) => new(label, null);

    /// <summary>A column whose value comes from the fact at <paramref name="factPath" />.</summary>
    public static FactViewColumn Fact(string label, string factPath) => new(label, factPath);

    /// <summary>
    /// A Properties-sheet column scoped to one specific list-dimension instance of
    /// <paramref name="factPath" /> — e.g. one particular <c>Custom[slug]</c> among many sharing
    /// the same structural path. Ordinary <see cref="Fact(string, string)" /> columns assume the
    /// path is unique per entity (true for every compiled view today); this overload is for the
    /// one case where it isn't (see <c>CustomFieldViewMerger</c>, docs/plans/user-provided.md).
    /// Only honored by <see cref="FactViewRenderer" />'s Properties rendering, not List.
    /// </summary>
    public static FactViewColumn Fact(string label, string factPath, string keyFilter) =>
        new(label, factPath, keyFilter);
}

/// <summary>How a fact view is shaped.</summary>
public enum FactViewKind
{
    /// <summary>
    /// One row per list-dimension element, attribute columns pivoted (thermal zones,
    /// pending updates, processes). Requires a list dimension.
    /// </summary>
    List,

    /// <summary>
    /// A property sheet of scalar device facts — each column becomes a
    /// (Property, Value) row (security posture, SNMP, battery). No list dimension.
    /// </summary>
    Properties,
}

/// <summary>
/// Which device-detail section group a view is filed under in the section nav. The device
/// detail page renders one vertical nav group per value (in this declared order), so every
/// device view must pick the group an operator would look under. Ignored for service views.
/// </summary>
public enum FactViewGroup
{
    /// <summary>Device Detail's incident+event timeline (see IncidentQueries.ListEntityHistoryAsync)
    /// — not a fact-view-library section, but placed in this enum so DeviceDetailModel's nav
    /// builder can group it alongside everything else.</summary>
    History,
    Hardware,
    Storage,
    Network,
    Software,
    Security,
    Protocols,
    Discovery,

    /// <summary>Operator-authored data (see <see cref="FactSource.ManualEntry" />) — custom
    /// field values with no dedicated existing-view home.</summary>
    Custom,
}

/// <summary>
/// Declares a display table built from a device's raw facts — NOT a stored projection.
/// For data that only ever appears on one device's detail page (thermal zones, pending
/// updates, encrypted volumes, the discovered-attribute long tail), a fact view turns the
/// flat facts_history stream into a labelled table at render time, with no proj_ table,
/// migration, index, or ingest-write cost. Reserve projections for data queried ACROSS
/// devices; default new device-detail data to a fact view (promote to a projection later
/// if a cross-device need appears).
/// </summary>
public sealed record FactViewDef(
    string Title,
    IReadOnlyList<FactViewColumn> Columns,
    FactViewKind Kind,
    FactViewGroup Group
);