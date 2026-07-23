namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandMethodParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    public bool IsConnection { get; set; }
    public bool IsCancellationToken { get; set; }

    /// <summary>The <c>sort</c>/<c>dir</c> variant selectors of a [SortableBy] method — they
    /// pick a command-text variant at runtime and are never bound to SQL.</summary>
    public bool IsSortSelector { get; set; }

    public bool IsBindParameter => !IsConnection && !IsCancellationToken && !IsSortSelector;
}