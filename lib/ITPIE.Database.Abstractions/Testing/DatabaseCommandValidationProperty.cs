namespace ITPIE.Database.Abstractions.Testing;

public sealed record DatabaseCommandValidationProperty
{
    // Strip underscores so camelCase property names normalize to match snake_case column names
    // (mirrors DatabaseCommandValidationColumn.NormalizeName). Also handles `_` discard-style placeholders.
    private static string NormalizeName(string value)
        => value.Replace("_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();

    public DatabaseCommandValidationProperty(string name, bool isNullable)
    {
        Name = name;
        IsNullable = isNullable;

        NormalizedName = NormalizeName(name);
    }

    public string Name { get; }

    public bool IsNullable { get; }

    /// <summary>
    /// Gets the normalized name of the property.
    /// </summary>
    public string NormalizedName { get; }
}