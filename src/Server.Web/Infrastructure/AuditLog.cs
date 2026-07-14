using System.Text.Json;

using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Audit;

public sealed class AuditLog
{
    private readonly NpgsqlDataSource _db;

    public AuditLog(NpgsqlDataSource db)
    {
        _db = db;
    }

    public async Task WriteAsync(
        string actor,
        string action,
        string? targetRef,
        object? detail = null,
        CancellationToken ct = default
    )
    {
        JsonElement? detailElement = detail is null
            ? null
            : JsonSerializer.SerializeToElement(detail);

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await conn.InsertAuditLogAsync(actor, action, targetRef, detailElement, ct).ExecuteAsync(ct);
    }

    /// <summary>
    /// Writes an audit entry on an existing connection/transaction, so it commits or rolls back
    /// atomically with the caller's other writes (review D12). The generated
    /// <c>InsertAuditLogAsync</c> can't be handed a transaction, so this builds the same insert
    /// by hand — the one place that does, instead of each transactional caller hand-rolling it.
    /// </summary>
    public static async Task WriteAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string actor,
        string action,
        string? targetRef,
        object? detail = null,
        CancellationToken ct = default
    )
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_log (actor, action, target_ref, detail)
            VALUES ($1, $2, $3, $4::jsonb)
            """;
        cmd.Parameters.Add(Param.Text(actor));
        cmd.Parameters.Add(Param.Text(action));
        cmd.Parameters.Add(Param.Text(targetRef));
        cmd.Parameters.Add(Param.Text(detail is null ? null : JsonSerializer.Serialize(detail)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}