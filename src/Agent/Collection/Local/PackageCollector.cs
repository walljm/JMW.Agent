using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Local;

public sealed class PackageCollector : OsDispatchLocalCollector
{
    private const int PackageCap = 2000;
    private static readonly ILogger<PackageCollector> Log = AgentLog.CreateLogger<PackageCollector>();

    public override string Name => "packages";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        if (CollectorHelper.BinaryExists("dpkg-query"))
        {
            await CollectDpkgAsync(deviceId, facts, ct);
        }
        else if (CollectorHelper.BinaryExists("rpm"))
        {
            await CollectRpmAsync(deviceId, facts, ct);
        }
        else if (CollectorHelper.BinaryExists("pacman"))
        {
            await CollectPacmanAsync(deviceId, facts, ct);
        }
    }

    private static async Task CollectDpkgAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync(
                "dpkg-query",
                "-W -f=${Package}\\t${Version}\\n",
                ct
            );
            EmitPackages(deviceId, facts, "apt", ParseTsvLines(output));
        }
        catch (Exception ex)
        {
            PackageCollectorLog.DpkgQueryFailed(Log, ex);
        }
    }

    private static async Task CollectRpmAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync(
                "rpm",
                "-qa --queryformat %{NAME}\\t%{VERSION}-%{RELEASE}\\n",
                ct
            );
            EmitPackages(deviceId, facts, "rpm", ParseTsvLines(output));
        }
        catch (Exception ex)
        {
            PackageCollectorLog.RpmQueryFailed(Log, ex);
        }
    }

    private static async Task CollectPacmanAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync("pacman", "-Q", ct);
            EmitPackages(deviceId, facts, "pacman", ParseSpaceLines(output));
        }
        catch (Exception ex)
        {
            PackageCollectorLog.PacmanQueryFailed(Log, ex);
        }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        if (!CollectorHelper.BinaryExists("brew"))
        {
            return;
        }

        try
        {
            string output = await CollectorHelper.RunAsync("brew", "list --versions", ct);
            EmitPackages(deviceId, facts, "brew", ParseSpaceLines(output));
        }
        catch (Exception ex)
        {
            PackageCollectorLog.BrewListFailed(Log, ex);
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        const string script = """
            $keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                      'HKLM:\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
            Get-ItemProperty $keys -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName } |
            Select-Object DisplayName, DisplayVersion |
            Sort-Object DisplayName |
            ConvertTo-Json -Compress
            """;

        try
        {
            List<WinPackageRow> rows = await CollectorHelper.RunPsJsonAsync<WinPackageRow>(script, ct);
            IEnumerable<(string Name, string Version)> packages = rows
                .Where(r => r.DisplayName is { Length: > 0 })
                .Select(r => (r.DisplayName!, r.DisplayVersion ?? ""))
                .OrderBy(p => p.Item1);
            EmitPackages(deviceId, facts, "windows", packages);
        }
        catch (Exception ex)
        {
            PackageCollectorLog.WindowsQueryFailed(Log, ex);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<(string Name, string Version)> ParseTsvLines(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t');
            if (tab < 1)
            {
                continue;
            }

            string name = line[..tab].Trim();
            string version = line[(tab + 1)..].Trim();
            if (name.Length > 0)
            {
                yield return (name, version);
            }
        }
    }

    private static IEnumerable<(string Name, string Version)> ParseSpaceLines(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = line.IndexOf(' ');
            if (space < 1)
            {
                continue;
            }

            string name = line[..space].Trim();
            string version = line[(space + 1)..].Trim();
            if (name.Length > 0)
            {
                yield return (name, version);
            }
        }
    }

    private static void EmitPackages(
        string deviceId,
        List<Fact> facts,
        string manager,
        IEnumerable<(string Name, string Version)> packages
    )
    {
        foreach ((string name, string version) in packages.OrderBy(p => p.Name).Take(PackageCap))
        {
            string[] keys = [deviceId, name];
            facts.Add(Fact.Create(FactPaths.PackageVersion, keys, version));
            facts.Add(Fact.Create(FactPaths.PackageManager, keys, manager));
        }
    }

    private sealed class WinPackageRow
    {
        public string? DisplayName { get; set; }
        public string? DisplayVersion { get; set; }
    }
}

internal static partial class PackageCollectorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "dpkg-query failed.")]
    internal static partial void DpkgQueryFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "rpm query failed.")]
    internal static partial void RpmQueryFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "pacman query failed.")]
    internal static partial void PacmanQueryFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "brew list failed.")]
    internal static partial void BrewListFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Windows installed-programs query failed.")]
    internal static partial void WindowsQueryFailed(ILogger logger, Exception ex);
}