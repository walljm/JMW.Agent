namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandResultParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    public bool HasNullableAnnotation { get; set; }
    public bool IsValueType { get; set; }
    public bool IsReferenceType => !IsValueType;
}