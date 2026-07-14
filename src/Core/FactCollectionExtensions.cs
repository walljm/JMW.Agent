namespace JMW.Discovery.Core;

/// <summary>
/// Collector conveniences for building a fact list. <see cref="AddIfPresent" /> collapses the
/// ubiquitous guard-then-add micro-pattern —
/// <c>if (v is { Length: &gt; 0 }) facts.Add(Fact.Create(path, keys, v));</c> — into one call
/// (review D4). It no-ops on null/empty, exactly matching the <c>is { Length: &gt; 0 }</c> /
/// <c>!string.IsNullOrEmpty</c> guard it replaces.
/// </summary>
public static class FactCollectionExtensions
{
    /// <summary>Adds a string-valued fact only when <paramref name="value" /> is non-null and non-empty.</summary>
    public static void AddIfPresent(
        this List<Fact> facts,
        string attributePath,
        string[] keys,
        string? value
    )
    {
        if (!string.IsNullOrEmpty(value))
        {
            facts.Add(Fact.Create(attributePath, keys, value));
        }
    }
}