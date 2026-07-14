namespace JMW.Discovery.Server.UI;

public sealed record FilterValue(string Value, string Label);

public sealed record FilterSpec(string Key, string Label, IReadOnlyList<FilterValue> Values);

public sealed class DataGridModel
{
    public required IReadOnlyList<FilterSpec> Filters { get; init; }

    public IReadOnlyDictionary<string, string> ActiveFilters { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Q { get; init; } = "";
    public string? NextCursor { get; init; }
    public string FragmentUrl { get; init; } = "";
    public string HtmxTarget { get; init; } = "";
    public string PageUrl { get; init; } = "";
}