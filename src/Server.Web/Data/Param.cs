using System.Diagnostics.CodeAnalysis;

using Npgsql;

using NpgsqlTypes;

namespace JMW.Discovery.Server.Data;

/// <summary>
/// Factory for <see cref="NpgsqlParameter" /> instances, centralizing the
/// <c>(object?)value ?? DBNull.Value</c> null handling and <see cref="NpgsqlDbType" /> wiring that
/// was hand-rolled at ~60 call sites across Ingest/Api/Infrastructure (review D8). Each method name
/// states the Postgres type it binds; callers still choose which one to call, so a mismatched type
/// is caught at compile time exactly as it was with the inline parameter.
/// </summary>
[SuppressMessage("ReSharper", "BitwiseOperatorOnEnumWithoutFlags")]
public static class Param
{
    public static NpgsqlParameter Text(string? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    public static NpgsqlParameter Integer(int value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Integer };

    public static NpgsqlParameter Bigint(long value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Bigint };

    public static NpgsqlParameter NullableInteger(int? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

    public static NpgsqlParameter TimestampTz(DateTimeOffset value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter NullableTimestampTz(DateTimeOffset? value) =>
        new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter Interval(TimeSpan value) =>
        new() { Value = value, NpgsqlDbType = NpgsqlDbType.Interval };

    public static NpgsqlParameter TextArray(string?[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    public static NpgsqlParameter SmallintArray(short[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint };

    public static NpgsqlParameter BigintArray(long?[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint };

    public static NpgsqlParameter DoubleArray(double?[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double };

    public static NpgsqlParameter TimestampTzArray(DateTimeOffset[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz };

    public static NpgsqlParameter UuidArray(Guid?[] values) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };
}