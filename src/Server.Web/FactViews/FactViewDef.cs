using JMW.Discovery.Core;

namespace JMW.Discovery.Server.FactViews;

/// <summary>
/// How a <see cref="FactViewColumn" />'s raw stored value is turned into display text. The
/// renderer applies the format to produce a <see cref="FactCell" /> carrying both the human
/// display string AND a machine sort value, so client-side sortable tables order humanized
/// columns (byte sizes, percentages) numerically instead of lexically. <see cref="None" /> passes
/// the value through unchanged.
/// </summary>
public enum FactViewFormat
{
    /// <summary>Raw value, unchanged; sort value = display.</summary>
    None,

    /// <summary>Byte count → "1.5 GB" (via ViewFormat.Bytes); sort value = raw byte count.</summary>
    Bytes,

    /// <summary>Bytes/sec → "1.5 GB/s" (via ViewFormat.Bytes + "/s"); sort value = raw bytes/sec.</summary>
    BytesPerSecond,

    /// <summary>Number → "42.5%"; sort value = the raw number.</summary>
    Percent,

    /// <summary>Number → "40 °C"; sort value = the raw number.</summary>
    Celsius,

    /// <summary>Unix epoch seconds → ISO-8601 UTC; sort value = the raw epoch.</summary>
    UnixSeconds,

    /// <summary>Seconds → compact duration "1h2m" (via ViewFormat.Duration); sort value = raw seconds.</summary>
    DurationSeconds,

    /// <summary>Boolean-ish value → "Yes"/"No"; sort value = display.</summary>
    Bool,

    /// <summary>MAC address → "Vendor (US)" via the render context's OUI resolver; sort value = display.</summary>
    Oui,
}

/// <summary>
/// One column of a <see cref="FactViewDef" />. A column is one of three shapes:
/// a dimension KEY (the list-element identity, e.g. the thermal zone name; no fact path),
/// a fact ATTRIBUTE whose value fills the cell (references a FactPaths constant so the
/// (DimKey, Attribute) routing grammar — shared with projections via <see cref="Fact" /> — is
/// derived, never hand-typed), or a COMPUTED column whose value is calculated at render time from
/// the row's other facts (see <see cref="Computed" />). <see cref="Format" /> controls how the raw
/// value is displayed; <see cref="Compute" /> is set only for computed columns.
/// </summary>
public sealed record FactViewColumn(
    string Label,
    string? FactPath,
    string? KeyFilter = null,
    FactViewFormat Format = FactViewFormat.None,
    Func<IReadOnlyDictionary<string, string?>, string?>? Compute = null,
    IReadOnlyList<string>? DependsOn = null
)
{
    // A record positional-parameter default must be a compile-time constant, so [] can't be the
    // default above; this body property overrides the auto-generated one and normalizes the null
    // default to an empty list, so every read site can treat DependsOn as always non-null.
    public IReadOnlyList<string> DependsOn { get; init; } = DependsOn ?? [];

    /// <summary>A column showing the row's dimension key (the list-element identity).</summary>
    public static FactViewColumn Key(string label) => new(label, null);

    /// <summary>A column whose value comes from the fact at <paramref name="factPath" />,
    /// optionally run through <paramref name="format" /> for display.</summary>
    public static FactViewColumn Fact(string label, string factPath, FactViewFormat format = FactViewFormat.None) =>
        new(label, factPath, Format: format);

    /// <summary>
    /// A Properties-sheet column scoped to one specific list-dimension instance of
    /// <paramref name="factPath" /> — e.g. one particular <c>Interface[mac]</c> among many sharing
    /// the same structural path. Ordinary <see cref="Fact(string, string, FactViewFormat)" /> columns
    /// assume the path is unique per entity (true for every compiled view today); this overload is
    /// for the case where it isn't. Only honored by <see cref="FactViewRenderer" />'s Properties
    /// rendering, not List.
    /// </summary>
    public static FactViewColumn Fact(string label, string factPath, string keyFilter) =>
        new(label, factPath, keyFilter);

    /// <summary>
    /// A column computed at render time from the row's other facts. <paramref name="compute" />
    /// receives the row's fact values keyed by their FactPaths constant (full templated path) and
    /// returns the cell's raw value, which <paramref name="format" /> then formats for display.
    /// If <paramref name="compute" /> reads a fact no <em>other</em> column in this view already
    /// selects, that path MUST be listed in <paramref name="dependsOn" /> — the renderer only pulls
    /// a fact into the row when some column (Fact or Computed-via-DependsOn) asks for it, so an
    /// undeclared dependency silently reads as absent (null) rather than throwing. Introduces no
    /// FactPath of its own — never touches the routing fitness test. List views only.
    /// </summary>
    public static FactViewColumn Computed(
        string label,
        Func<IReadOnlyDictionary<string, string?>, string?> compute,
        FactViewFormat format = FactViewFormat.None,
        IReadOnlyList<string>? dependsOn = null
    ) => new(label, null, Compute: compute, Format: format, DependsOn: dependsOn ?? []);
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
    /// <summary>Device Detail's landing dashboard (identity, at-a-glance counts, collection
    /// health, recent activity) — not a fact-view-library section, but placed first in this enum
    /// so it's the default tab and DeviceDetailModel's nav builder can group it alongside
    /// everything else.</summary>
    Summary,

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

    Interfaces,
    Containers,
    System,
    /// <summary>Operator-authored data (see <see cref="FactSource.ManualEntry" />) — custom
    /// field values with no dedicated existing-view home.</summary>
    Custom,
}

/// <summary>
/// Section-nav presentation for <see cref="FactViewGroup" /> — the single source of truth the
/// device- and service-detail pages read so neither keeps its own group list. Display order is
/// the enum's declared order (iterate <see cref="Ordered" />), so a newly declared group appears
/// in the nav automatically with no page edit — closing the gap where a group added to the enum
/// but forgotten in a page's hardcoded order array silently dropped its views.
/// </summary>
public static class FactViewGroups
{
    /// <summary>Every group in section-nav display order (the enum's declared order).</summary>
    public static readonly IReadOnlyList<FactViewGroup> Ordered = Enum.GetValues<FactViewGroup>();

    /// <summary>
    /// The section-nav label for a group. Identical to the enum name for every group today; add a
    /// switch arm here only when a label needs characters a C# identifier can't carry.
    /// </summary>
    public static string DisplayName(this FactViewGroup group) => group.ToString();
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