using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ITPIE.Migrations;

public static class DatabaseMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Adds database migration services for a PostgreSQL database.
    /// </summary>
    public static IServiceCollection AddDatabaseMigrations(
        this IServiceCollection services,
        Action<DatabaseMigrationOptions>? configure = null
    )
    {
        services.AddOptions<DatabaseMigrationOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        RegisterCoreServices(services);

        return services;
    }

    /// <summary>
    /// Adds database migration services for a PostgreSQL database with configuration binding.
    /// </summary>
    public static IServiceCollection AddDatabaseMigrations(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddOptions<DatabaseMigrationOptions>()
            .Bind(configuration.GetSection("Migrations"));

        RegisterCoreServices(services);

        return services;
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.AddSingleton<MigrationCompletedSignal>();
        services.AddSingleton<DatabaseMigrationEngine>();
        services.AddSingleton(sp =>
            {
                DatabaseMigrationOptions options = sp.GetRequiredService<IOptions<DatabaseMigrationOptions>>().Value;
                Assembly assembly = options.ScriptsAssembly
                 ?? Assembly.GetEntryAssembly()
                 ?? throw new InvalidOperationException(
                        "ScriptsAssembly must be set in DatabaseMigrationOptions when entry assembly is not available."
                    );
                return new DatabaseMigrationScriptProvider(assembly);
            }
        );
        services.AddHostedService<DatabaseMigrationService>();
    }
}