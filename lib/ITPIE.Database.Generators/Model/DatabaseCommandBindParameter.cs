namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandBindParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ElementType { get; set; } = string.Empty;

    public bool IsCollection { get; set; }
    public bool IsNullableOfType { get; set; }

    public string ValidateDefaultValue { get; set; } = "default";
}