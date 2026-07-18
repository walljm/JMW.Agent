using System.Reflection;

namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// The server's own build identity, read once from the entry assembly's
/// AssemblyInformationalVersion. The SDK stamps that attribute as
/// "&lt;version&gt;+&lt;git-sha&gt;" (SourceLink appends the commit automatically), so this is
/// the single source of truth for "what build is this deployment running" — logged at
/// startup and shown in the UI sidebar.
/// </summary>
public static class ServerVersion
{
    /// <summary>The full informational version, e.g. "3.1.0+3de2ebdfbe9f…".</summary>
    public static readonly string Full =
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    /// <summary>The semver portion, e.g. "3.1.0".</summary>
    public static readonly string Version = SplitVersion(Full).Version;

    /// <summary>Short commit hash the build was cut from ("" when no commit metadata).</summary>
    public static readonly string ShortCommit = SplitVersion(Full).ShortCommit;

    /// <summary>Compact display form: "3.1.0 (3de2ebd)" or just the version without commit metadata.</summary>
    public static string Display =>
        ShortCommit.Length == 0 ? Version : $"{Version} ({ShortCommit})";

    private static (string Version, string ShortCommit) SplitVersion(string full)
    {
        int plus = full.IndexOf('+');
        if (plus < 0)
        {
            return (full, string.Empty);
        }

        string commit = full[(plus + 1)..];
        return (full[..plus], commit.Length > 7 ? commit[..7] : commit);
    }
}