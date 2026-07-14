using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;

using Npgsql;
using Npgsql.Schema;

namespace ITPIE.Database.Abstractions.Testing;

public static class DatabaseCommandValidatorExtensions
{
    /// <summary>
    /// Executes a command for validation only.
    /// </summary>
    /// <remarks>
    /// Executes the command with <see cref="CommandBehavior.SchemaOnly" /> and <see cref="CommandBehavior.KeyInfo" /> to
    /// produce a column schema, before executing the command normally to validate bind parameters. Ignores certain
    /// errors unrelated to bind parameter validations, such as foreign key violations, which cannot be properly
    /// validated by this method.
    /// </remarks>
    /// <param name="connection"></param>
    /// <param name="commandText"></param>
    /// <param name="parameters"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>The column schema, including key info.</returns>
    /// <exception cref="PostgresException" />
    public static async Task<ReadOnlyCollection<DbColumn>> ValidateAsync(
        this NpgsqlConnection connection,
        string commandText,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken cancellationToken
    )
    {
        ReadOnlyCollection<DbColumn> columns = new([]);

        try
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = new(commandText, connection, transaction);

            foreach (NpgsqlParameter parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(
                CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo,
                cancellationToken
            ))
            {
                ReadOnlyCollection<NpgsqlDbColumn> npgsqlColumns = await reader.GetColumnSchemaAsync(cancellationToken);
                columns = new ReadOnlyCollection<DbColumn>(npgsqlColumns.Cast<DbColumn>().ToList());
            }

            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    // this block intentionally left blank
                }
            }

            await transaction.RollbackAsync(cancellationToken);
        }
        catch (PostgresException e) when (
            e.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation
        )
        {
            // this block intentionally left blank
        }

        return columns;
    }
}