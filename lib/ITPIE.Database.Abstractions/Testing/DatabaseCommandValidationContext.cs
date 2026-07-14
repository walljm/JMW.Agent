using System.Data.Common;
using System.Text;

namespace ITPIE.Database.Abstractions.Testing;

public sealed record DatabaseCommandValidationContext(
    IReadOnlyList<DatabaseCommandValidationColumn> Columns,
    IReadOnlyList<DatabaseCommandValidationProperty> Properties
)
{
    #region PostgreSQL Methods

    /// <summary>
    /// Gets the default value expression for the given PostgreSQL type.
    /// </summary>
    /// <remarks>
    /// For use in command text building. Keys are <see cref="DbColumn.DataTypeName" /> values as produced by Npgsql,
    /// which uses the user-facing PostgreSQL type names (e.g. <c>integer</c>, <c>timestamp with time zone</c>,
    /// <c>integer[]</c>) rather than the <c>pg_type.typname</c> internal form (<c>int4</c>, <c>timestamptz</c>,
    /// <c>_int4</c>).
    /// </remarks>
    /// <exception cref="NotImplementedException">Thrown when the data type name is not recognized.</exception>
    private static string GetDefaultValueExpression(string postgresTypeName) => postgresTypeName switch
    {
        "bit" => "B'0'",
        "boolean" => "'false'::bool",
        "bytea" => "'\\xDEADBEEF'::bytea",
        "character" => "' '::char",
        "cidr" => "'0.0.0.0/0'::cidr",
        "date" => "'1970-01-01'::date",
        "daterange" => "'(1970-01-01, 1970-01-01)'::daterange",
        "double precision" => "0.0::float8",
        "inet" => "'0.0.0.0'::inet",
        "smallint" => "0::int2",
        "integer" => "0::int4",
        "int4range" => "int4range(0, 0)::int4range",
        "bigint" => "0::int8",
        "int8range" => "int8range(0, 0)::int8range",
        "interval" => "'00:00:00'::interval",
        "json" => "'{}'::json",
        "jsonb" => "'{}'::jsonb",
        "macaddr" => "'00:00:00:00:00:00'::macaddr",
        "macaddr8" => "'00:00:00:00:00:00:00'::macaddr8",
        "money" => "0.0::money",
        "numeric" => "0.0::numeric",
        "numrange" => "'(0, 0)'::numrange",
        "real" => "0.0::float4",
        "text" => "''::text",
        "time without time zone" => "'00:00:00'::time",
        "time with time zone" => "'00:00:00+00'::timetz",
        "timestamp without time zone" => "'1970-01-01 00:00:00'::timestamp",
        "timestamp with time zone" => "'1970-01-01 00:00:00+00'::timestamptz",
        "tsquery" => "'fat & rat'::tsquery",
        "tsrange" => "'(1970-01-01 00:00, 1970-01-01 00:00)'::tsrange",
        "tstzrange" => "'(1970-01-01 00:00:00+00, 1970-01-01 00:00:00+00)'::tstzrange",
        "tsvector" => "'a:1A fat:2B,4C cat:5D'::tsvector",
        "uuid" => "'00000000-0000-0000-0000-000000000000'::uuid",
        "bit varying" => "B'0'",
        "character varying" => "''::varchar",
        "xml" => "'<foo>bar</foo>'::xml",

        // citext is an extension type, so DataTypeName arrives schema-qualified (e.g. "public.citext").
        // Match by suffix to support any install schema. The bare "citext" form is a fallback in case
        // the extension is ever installed into pg_catalog.
        "citext" => "''::citext",
        { } s when s.EndsWith(".citext", StringComparison.Ordinal) => "''::citext",

        "bit[]" => "ARRAY[B'0']",
        "boolean[]" => "ARRAY['false'::bool]",
        "bytea[]" => "ARRAY['\\xDEADBEEF'::bytea]",
        "character[]" => "ARRAY[' '::char]",
        "cidr[]" => "ARRAY['0.0.0.0/0'::cidr]",
        "date[]" => "ARRAY['1970-01-01'::date]",
        "daterange[]" => "ARRAY['(1970-01-01, 1970-01-01)'::daterange]",
        "double precision[]" => "ARRAY[0.0::float8]",
        "inet[]" => "ARRAY['0.0.0.0'::inet]",
        "smallint[]" => "ARRAY[0::int2]",
        "integer[]" => "ARRAY[0::int4]",
        "int4range[]" => "ARRAY[int4range(0, 0)::int4range]",
        "bigint[]" => "ARRAY[0::int8]",
        "int8range[]" => "ARRAY[int8range(0, 0)::int8range]",
        "interval[]" => "ARRAY['00:00:00'::interval]",
        "json[]" => "ARRAY['{}'::json]",
        "jsonb[]" => "ARRAY['{}'::jsonb]",
        "macaddr[]" => "ARRAY['00:00:00:00:00:00'::macaddr]",
        "macaddr8[]" => "ARRAY['00:00:00:00:00:00:00'::macaddr8]",
        "money[]" => "ARRAY[0.0::money]",
        "numeric[]" => "ARRAY[0.0::numeric]",
        "numrange[]" => "ARRAY['(0, 0)'::numrange]",
        "real[]" => "ARRAY[0.0::float4]",
        "text[]" => "ARRAY[''::text]",
        "time without time zone[]" => "ARRAY['00:00:00'::time]",
        "time with time zone[]" => "ARRAY['00:00:00+00'::timetz]",
        "timestamp without time zone[]" => "ARRAY['1970-01-01 00:00:00'::timestamp]",
        "timestamp with time zone[]" => "ARRAY['1970-01-01 00:00:00+00'::timestamptz]",
        "tsquery[]" => "ARRAY['fat & rat'::tsquery]",
        "tsrange[]" => "ARRAY['(1970-01-01 00:00, 1970-01-01 00:00)'::tsrange]",
        "tstzrange[]" => "ARRAY['(1970-01-01 00:00:00+00, 1970-01-01 00:00:00+00)'::tstzrange]",
        "tsvector[]" => "ARRAY['a:1A fat:2B,4C cat:5D'::tsvector]",
        "uuid[]" => "ARRAY['00000000-0000-0000-0000-000000000000'::uuid]",
        "bit varying[]" => "ARRAY[B'0']",
        "character varying[]" => "ARRAY[''::varchar]",
        "xml[]" => "ARRAY['<foo>bar</foo>'::xml]",

        "citext[]" => "ARRAY[''::citext]",
        { } s when s.EndsWith(".citext[]", StringComparison.Ordinal) => "ARRAY[''::citext]",

        _ => throw new NotImplementedException(
            $"The name '{postgresTypeName}' is not recognized as a PostgreSQL type name."
        ),
    };

    /// <summary>
    /// Gets the command text that selects default value expressions for each column in a given row description.
    /// </summary>
    private static string GetValidationCommandText(
        IReadOnlyList<(int Index, DatabaseCommandValidationColumn Column, DatabaseCommandValidationProperty Property)>
            context
    )
    {
        StringBuilder builder = new(256);

        builder.AppendLine("SELECT");

        if (context.Count == 0)
        {
            return builder.ToString();
        }

        for (int i = 0; i < context.Count; i++)
        {
            if (i == 0)
            {
                builder.AppendLine($"  {context[i].Column.Name}");
            }
            else
            {
                builder.AppendLine($"  , {context[i].Column.Name}");
            }
        }

        builder.AppendLine("FROM (");
        builder.AppendLine("  VALUES (");

        for (int i = 0; i < context.Count; i++)
        {
            string value = GetDefaultValueExpression(context[i].Column.PostgresTypeName);

            if (i == 0)
            {
                builder.AppendLine($"    {value}");
            }
            else
            {
                builder.AppendLine($"    , {value}");
            }
        }

        if (context.Any(static ctx => ctx.Property.IsNullable))
        {
            builder.AppendLine("  ), (");

            for (int i = 0; i < context.Count; i++)
            {
                string value = context[i].Property.IsNullable
                    ? "null"
                    : GetDefaultValueExpression(context[i].Column.PostgresTypeName);

                if (i == 0)
                {
                    builder.AppendLine($"    {value}");
                }
                else
                {
                    builder.AppendLine($"    , {value}");
                }
            }
        }

        builder.AppendLine("  )");
        builder.Append(") AS t1 (");

        for (int i = 0; i < context.Count; i++)
        {
            if (i != 0)
            {
                builder.Append(", ");
            }

            builder.Append($"{context[i].Column.Name}");
        }

        builder.AppendLine(")");

        return builder.ToString();
    }

    #endregion

    #region Validation Methods

    /// <summary>
    /// Validates that the number of properties matches the number of columns.
    /// </summary>
    private static DatabaseCommandValidationError? ValidateCount(int columnCount, int propertyCount)
        => columnCount == propertyCount
            ? null
            : new DatabaseCommandValidationError(
                $"Property count mismatch. Properties: {propertyCount}. Columns: {columnCount}."
            );

    /// <summary>
    /// Validates that the nullability of the column matches the nullability of the type.
    /// </summary>
    /// <remarks>
    /// That is, whether a nullable column value might be read into a non-nullable C# type. Nullability is unknown
    /// unless the select is made from a well-defined table and column. The following cannot be determined:
    /// <list type="bullet">
    ///     <item>SELECT from a [temporary] table created during the current transaction</item>
    ///     <item>SELECT scalar or null literal</item>
    /// </list>
    /// The truth table implemented in this method is:
    /// <code>
    /// | Column           | C#         | Result |
    /// |------------------|------------|--------|
    /// | true  (    NULL) | false (T)  | ❌     |
    /// | true  (    NULL) | true  (T?) | ✅     |
    /// | false (NOT NULL) | false (T)  | ✅     |
    /// | false (NOT NULL) | true  (T?) | ❌     |
    /// </code>
    /// </remarks>
    private static DatabaseCommandValidationError? ValidateNullability(
        int index,
        DatabaseCommandValidationColumn column,
        DatabaseCommandValidationProperty property
    )
        => column.IsNullable == property.IsNullable
            ? null
            : new(
                $"Nullability mismatch. Property '{property.Name}' is {(property.IsNullable ? "T?" : "T")}. Column[{index}] '{column.DisplayName}' is {(column.IsNullable ? "NULL" : "NOT NULL")}"
            );

    /// <summary>
    /// Validates that the normalized column and property names match.
    /// </summary>
    private static DatabaseCommandValidationError? ValidateNames(
        int index,
        DatabaseCommandValidationColumn column,
        DatabaseCommandValidationProperty property
    )
        => string.Equals(column.NormalizedName, property.NormalizedName, StringComparison.OrdinalIgnoreCase)
            ? null
            : new($"Name mismatch. Property is '{property.Name}'. Column[{index}] is '{column.DisplayName}'.");

    #endregion

    public DatabaseCommandValidationContext(
        IReadOnlyCollection<DbColumn> columns,
        IReadOnlyList<DatabaseCommandValidationProperty> properties
    )
        : this(
            Columns: columns.Select(column => new DatabaseCommandValidationColumn(column)).ToArray(),
            Properties: properties
        )
    {
        // This ctor is used to create a context from the schema collection returned by the Npgsql data reader.
    }

    /// <summary>
    /// Gets the zipped column and property validation contexts with index.
    /// </summary>
    private IEnumerable<(int Index, DatabaseCommandValidationColumn Column, DatabaseCommandValidationProperty Property)>
        IndexedValidationContext
        => Columns.Zip(Properties).Select(static (pair, index) => (index, pair.First, pair.Second));

    /// <summary>
    /// Gets the validation result.
    /// </summary>
    public DatabaseCommandValidationResult GetValidationResult()
    {
        List<DatabaseCommandValidationError> errors = [];

        if (ValidateCount(Columns.Count, Properties.Count) is { } validateCountError)
        {
            errors.Add(validateCountError);
        }

        if (errors.Count == 0)
        {
            foreach ((int index, DatabaseCommandValidationColumn column, DatabaseCommandValidationProperty property) in
                IndexedValidationContext)
            {
                if (ValidateNames(index, column, property) is { } validateNameError)
                {
                    errors.Add(validateNameError);
                }

                if (ValidateNullability(index, column, property) is { } validateNullabilityError)
                {
                    errors.Add(validateNullabilityError);
                }
            }
        }

        return new DatabaseCommandValidationResult(errors);
    }

    /// <summary>
    /// Validates!
    /// </summary>
    /// <exception cref="InvalidOperationException" />
    public void Validate()
    {
        DatabaseCommandValidationResult result = GetValidationResult();

        if (result.Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Validation failed.{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors.Select(error => $"  {error.Message}"))}"
        );
    }

    /// <inheritdoc
    ///     cref="GetValidationCommandText(IReadOnlyList{ValueTuple{int, DatabaseCommandValidationColumn, DatabaseCommandValidationProperty}})" />
    public string GetValidationCommandText()
        => GetValidationCommandText(IndexedValidationContext.ToArray());
}