namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandResult
{
    public string Type { get; set; } = string.Empty;

    public bool IsValueTuple { get; set; }
    public bool IsSingleField { get; set; }

    public IList<DatabaseCommandResultParameter> Parameters { get; } = [];
}