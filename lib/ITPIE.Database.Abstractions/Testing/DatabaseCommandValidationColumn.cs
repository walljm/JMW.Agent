using System.Data.Common;
using System.Text;

namespace ITPIE.Database.Abstractions.Testing;

public sealed record DatabaseCommandValidationColumn
{
    private static string ToDisplayString(DbColumn column)
    {
        StringBuilder builder = new(256);

        if (!string.IsNullOrEmpty(column.BaseSchemaName))
        {
            builder.Append(column.BaseSchemaName).Append('.');
        }

        if (!string.IsNullOrEmpty(column.BaseTableName))
        {
            builder.Append(column.BaseTableName).Append('.');
        }

        builder.Append(column.ColumnName);

        return builder.ToString();
    }

    private static string NormalizeName(string value)
        => value.Replace("_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();

    public DatabaseCommandValidationColumn(string name, string postgresTypeName, bool isNullable)
    {
        Name = name;
        PostgresTypeName = postgresTypeName;
        IsNullable = isNullable;

        DisplayName = name;
        NormalizedName = NormalizeName(name);
    }

    public DatabaseCommandValidationColumn(DbColumn column)
        : this(
            column.ColumnName,
            column.DataTypeName
         ?? throw new InvalidOperationException($"Column '{column.ColumnName}' has no DataTypeName."),
            column.AllowDBNull ?? true
        )
    {
        DisplayName = ToDisplayString(column);
    }

    public string Name { get; }

    public string PostgresTypeName { get; }

    public bool IsNullable { get; }

    /// <summary>
    /// Gets the fully qualified name of the column in the format: <code>"[{schema}.][{table}.]{column}"</code>
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the normalized name of the column.
    /// </summary>
    public string NormalizedName { get; }
}