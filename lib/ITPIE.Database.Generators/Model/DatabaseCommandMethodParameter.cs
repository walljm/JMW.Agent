namespace ITPIE.Database.Generators.Model;

public sealed class DatabaseCommandMethodParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    public bool IsConnection { get; set; }
    public bool IsCancellationToken { get; set; }

    public bool IsBindParameter => !IsConnection && !IsCancellationToken;
}