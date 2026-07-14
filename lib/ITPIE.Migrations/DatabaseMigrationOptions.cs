using System.Reflection;

namespace ITPIE.Migrations;

/// <summary>
/// Options for database migrations.
/// </summary>
public sealed class DatabaseMigrationOptions
{
    /// <summary>
    /// True to print the migration scripts that would be executed without executing them.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// True to skip running migrations entirely. Useful for E2E tests where migrations
    /// are run manually before the application starts.
    /// </summary>
    public bool Skip { get; set; }

    /// <summary>
    /// The assembly containing embedded migration SQL scripts in a Scripts/Ordered/ folder.
    /// Defaults to the entry assembly if not set.
    /// </summary>
    public Assembly? ScriptsAssembly { get; set; }
}