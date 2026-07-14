using System.Runtime.CompilerServices;

using Npgsql;

namespace ITPIE.Migrations;

internal static class DatabaseMigrationQueries
{
    private const string SchemaName = "public";

    /// <summary>
    /// Queries the information schema for the existence of the schemaversions table.
    /// </summary>
    /// <remarks>Compatible with DbUp Core (5.0) and DbUp PostgreSQL (5.0)</remarks>
    /// <returns>True if the schemaversions table exists; otherwise, false.</returns>
    public static async Task<bool> SchemaVersionsTableExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default
    )
    {
        const string commandText = $"""
            SELECT EXISTS (
                SELECT
                FROM
                    information_schema.tables
                WHERE
                        table_schema = '{SchemaName}'
                    AND table_name   = 'schemaversions'
            )
            """;

        await using NpgsqlCommand command = new(commandText, connection);

        return await command.ExecuteScalarAsync(cancellationToken) is bool exists && exists;
    }

    /// <summary>
    /// Creates the itpie_agent schema and schemaversions table.
    /// </summary>
    public static async Task CreateSchemaVersionsTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default
    )
    {
        const string commandText = $"""
            CREATE SCHEMA IF NOT EXISTS {SchemaName};

            CREATE TABLE IF NOT EXISTS {SchemaName}.schemaversions (
                schemaversionsid SERIAL PRIMARY KEY
                , scriptname TEXT NOT NULL
                , applied TIMESTAMPTZ NOT NULL
            )
            """;

        await using NpgsqlCommand command = new(commandText, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Gets all rows from the schemaversions table.
    /// </summary>
    /// <remarks>Compatible with DbUp Core (5.0) and DbUp PostgreSQL (5.0)</remarks>
    public static async IAsyncEnumerable<SchemaVersionsRow> GetSchemaVersionsAsync(
        NpgsqlConnection connection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        const string commandText = $"""
            SELECT
                schemaversionsid
                , scriptname
                , applied
            FROM
                {SchemaName}.schemaversions
            ORDER BY
                schemaversionsid
            """;

        await using NpgsqlCommand command = new(commandText, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int field0 = await reader.GetFieldValueAsync<int>(0, cancellationToken);
            string field1 = await reader.GetFieldValueAsync<string>(1, cancellationToken);
            DateTimeOffset field2 = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken);

            yield return new SchemaVersionsRow(field0, field1, field2);
        }
    }

    /// <summary>
    /// Gets the current row from the schemaversions table.
    /// </summary>
    /// <remarks>Compatible with DbUp Core (5.0) and DbUp PostgreSQL (5.0)</remarks>
    public static async Task<SchemaVersionsRow?> GetCurrentSchemaVersionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default
    )
    {
        const string commandText = $"""
            SELECT
                schemaversionsid
                , scriptname
                , applied
            FROM
                {SchemaName}.schemaversions
            ORDER BY
                schemaversionsid DESC
            LIMIT 1
            """;

        await using NpgsqlCommand command = new(commandText, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int field0 = await reader.GetFieldValueAsync<int>(0, cancellationToken);
            string field1 = await reader.GetFieldValueAsync<string>(1, cancellationToken);
            DateTimeOffset field2 = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken);

            return new SchemaVersionsRow(field0, field1, field2);
        }

        return null;
    }

    /// <summary>
    /// Writes a new row to the schemaversions table.
    /// </summary>
    /// <remarks>Compatible with DbUp Core (5.0) and DbUp PostgreSQL (5.0)</remarks>
    public static async Task<SchemaVersionsRow?> WriteSchemaVersionAsync(
        NpgsqlConnection connection,
        string scriptName,
        CancellationToken cancellationToken = default
    )
    {
        const string commandText = $"""
            INSERT INTO
                {SchemaName}.schemaversions (scriptname, applied)
            VALUES ($1, $2)
            RETURNING
                schemaversionsid
                , scriptname
                , applied
            """;

        await using NpgsqlCommand command = new(commandText, connection);
        command.Parameters.Add(
            new NpgsqlParameter<string>
            {
                TypedValue = scriptName,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset>
            {
                TypedValue = DateTimeOffset.UtcNow,
            }
        );
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int field0 = await reader.GetFieldValueAsync<int>(0, cancellationToken);
            string field1 = await reader.GetFieldValueAsync<string>(1, cancellationToken);
            DateTimeOffset field2 = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken);

            return new SchemaVersionsRow(field0, field1, field2);
        }

        return null;
    }

    /// <summary>
    /// Executes a migration command.
    /// </summary>
    /// <remarks>SQL comes from trusted embedded resource scripts, not user input.</remarks>
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities - SQL comes from trusted embedded resources
    public static async Task ExecuteMigrationAsync(
        NpgsqlConnection connection,
        string commandText,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default
    )
    {
        await using NpgsqlCommand command = new(commandText, connection);

        if (commandTimeout.HasValue)
        {
            command.CommandTimeout = (int)commandTimeout.Value.TotalSeconds;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
#pragma warning restore CA2100
}