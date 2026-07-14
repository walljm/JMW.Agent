namespace ITPIE.Migrations;

/// <summary>
/// A row in the schemaversions table.
/// </summary>
/// <remarks>Compatible with DbUp Core (5.0) and DbUp PostgreSQL (5.0)</remarks>
/// <param name="SchemaVersionsId">The primary key</param>
/// <param name="ScriptName">The filename of the script</param>
/// <param name="Applied">The timestamp that the script was applied</param>
internal sealed record SchemaVersionsRow(
    int SchemaVersionsId,
    string ScriptName,
    DateTimeOffset Applied
);