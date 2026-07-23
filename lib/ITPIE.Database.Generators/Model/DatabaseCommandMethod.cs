namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandMethod
{
    public DatabaseCommandClass? Parent { get; set; }

    public string Name { get; set; } = string.Empty;
    public string FullyQualifiedIdentifier { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public bool IsExtensionMethod { get; set; }

    public IList<DatabaseCommandMethodParameter> Parameters { get; } = [];
    public IList<DatabaseCommandBindParameter> BindParameters { get; } = [];

    /// <summary>[SortableBy] declarations in source order; first is the default sort key.
    /// Empty for plain (single-command-text) methods.</summary>
    public IList<(string Key, string SqlExpression)> SortableColumns { get; } = [];

    public DatabaseCommandText CommandText { get; } = new();
    public DatabaseCommandResult Result { get; } = new();

    public bool HasErrors { get; set; }
}