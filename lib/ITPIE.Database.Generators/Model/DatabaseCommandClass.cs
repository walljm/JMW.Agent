namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandClass
{
    public DatabaseCommandClass? Parent { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;

    public IList<DatabaseCommandMethod> Methods { get; } = [];
}