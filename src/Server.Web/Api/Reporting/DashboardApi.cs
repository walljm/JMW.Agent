using System.Globalization;

using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class DashboardApi
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/summary", GetSummary)
            .RequireAuthorization(ReadPolicy.Name)
            .CacheOutput(p => p.Expire(TimeSpan.FromSeconds(30)));
    }

    private static async Task<IResult> GetSummary(NpgsqlDataSource db, CancellationToken ct)
    {
        DashboardSummary summary = await LoadAsync(db, ct);
        return Results.Ok(summary);
    }

    public static async Task<DashboardSummary> LoadAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        (long? TotalDevices, long? ManagedDevices, long? DiscoveredDevices, long? TotalAgents, long? ApprovedAgents,
            long? PendingAgents) row = await conn.GetDashboardSummaryAsync(ct).FirstOrDefaultAsync(ct);

        long conflictsCount = await CountConflictsAsync(conn, ct);

        return new DashboardSummary(
            TotalDevices: row.TotalDevices ?? 0,
            ManagedDevices: row.ManagedDevices ?? 0,
            DiscoveredDevices: row.DiscoveredDevices ?? 0,
            TotalAgents: row.TotalAgents ?? 0,
            ApprovedAgents: row.ApprovedAgents ?? 0,
            PendingAgents: row.PendingAgents ?? 0,
            ConflictsCount: conflictsCount
        );
    }

    internal static async Task<long> CountConflictsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT 1
                FROM device_fingerprints df
                WHERE NOT EXISTS (
                    SELECT 1 FROM excluded_fingerprints ef
                    WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
                )
                GROUP BY df.fp_type, df.fp_value
                HAVING COUNT(DISTINCT df.device_id) > 1
            ) t
            """;
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : Convert.ToInt64(result ?? 0, CultureInfo.InvariantCulture);
    }
}

public sealed record DashboardSummary(
    long TotalDevices,
    long ManagedDevices,
    long DiscoveredDevices,
    long TotalAgents,
    long ApprovedAgents,
    long PendingAgents,
    long ConflictsCount
);