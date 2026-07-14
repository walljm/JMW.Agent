namespace ITPIE.Database.Abstractions.Testing;

public sealed record DatabaseCommandValidationResult(IReadOnlyList<DatabaseCommandValidationError> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}