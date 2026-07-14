using ITPIE.Database.Abstractions.Testing;

using JMW.Discovery.Server.Audit;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Validates every [DatabaseCommand] query against the live schema, using the shared
/// Testcontainers Postgres provided by the "Integration" collection fixture.
/// </summary>
[Collection("Integration")]
public sealed class ServerQueryValidationTests
{
    private readonly IntegrationFixture _fixture;

    public ServerQueryValidationTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
    }


    // AuditLog is any non-static type from the Server.Web assembly —
    // DatabaseCommandValidatorDataSource<T> only needs it to locate the assembly.
    public static IEnumerable<object[]> Validators =>
        new DatabaseCommandValidatorDataSource<AuditLog>();

    [Theory]
    [MemberData(nameof(Validators))]
    public async Task Query_MatchesLiveSchema(DatabaseCommandValidator validator)
    {
        // The collection fixture is always injected by xUnit, so the shared
        // Testcontainers database is available for every test case.
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();

        // Validator runs the SQL with SchemaOnly + KeyInfo, then rolls back.
        // Throws InvalidOperationException if column names, count, or nullability mismatch.
        await validator(conn, CancellationToken.None);
    }
}