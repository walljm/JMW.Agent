using ITPIE.Migrations;

using JMW.Discovery.Server.Audit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

namespace JMW.Discovery.Tests;

/// <summary>
/// Applies the production migration chain (the embedded <c>Scripts/Ordered/*.sql</c>
/// run by <see cref="ITPIE.Migrations" />) to a test database. Integration tests run
/// against the exact schema production runs — there is no separate Schema.sql snapshot
/// to hand-maintain or drift.
/// </summary>
internal static class MigrationTestRunner
{
    /// <summary>
    /// Runs every pending migration against the database at <paramref name="connectionString" />.
    /// Uses a dedicated data source that is disposed here, so the caller's data source
    /// (used for the actual test queries) is untouched.
    /// </summary>
    public static async Task ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddDatabaseMigrations(options => options.ScriptsAssembly = typeof(AuditLog).Assembly);

        await using ServiceProvider provider = services.BuildServiceProvider();
        MigrationCompletedSignal signal = provider.GetRequiredService<MigrationCompletedSignal>();

        // Start the migration hosted service (the exact production code path) and wait
        // for it to finish. Completed faults if any migration script throws.
        foreach (IHostedService hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        await signal.Completed;
    }
}