using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects OS identity facts: hostname, kernel, distro, boot time, timezone.
/// </summary>
public sealed class OsCollector : OsDispatchLocalCollector
{
    public override string Name => "os";

    protected override void CollectCommon(string deviceId, List<Fact> facts)
    {
        facts.Add(Fact.Create(FactPaths.SystemHostname, [deviceId], Environment.MachineName));
        facts.Add(Fact.Create(FactPaths.SystemOsFamily, [deviceId], DetectFamily()));
        facts.Add(Fact.Create(FactPaths.SystemKernelArch, [deviceId], RuntimeInformation.OSArchitecture.ToString()));
        facts.Add(Fact.Create(FactPaths.SystemTimezone, [deviceId], TimeZoneInfo.Local.Id));
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override Task CollectLinuxAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        try
        {
            facts.Add(
                Fact.Create(FactPaths.SystemKernel, [deviceId], File.ReadAllText("/proc/sys/kernel/osrelease").Trim())
            );
        }
        catch { }

        try
        {
            Dictionary<string, string> kv = KeyValueParser.ParseEqualsKeyValue(File.ReadLines("/etc/os-release"));
            if (kv.TryGetValue("NAME", out string? name))
            {
                facts.Add(Fact.Create(FactPaths.SystemOsDistro, [deviceId], name));
            }

            if (kv.TryGetValue("VERSION", out string? version))
            {
                facts.Add(Fact.Create(FactPaths.SystemOsVersion, [deviceId], version));
            }

            if (kv.TryGetValue("VERSION_ID", out string? build))
            {
                facts.Add(Fact.Create(FactPaths.SystemOsBuild, [deviceId], build));
            }
        }
        catch { }

        try
        {
            string[] parts = File.ReadAllText("/proc/uptime").Split(' ');
            if (double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double uptimeSecs
            ))
            {
                facts.Add(
                    Fact.Create(
                        FactPaths.SystemBootTime,
                        [deviceId],
                        DateTimeOffset.UtcNow.AddSeconds(-uptimeSecs).ToString("o")
                    )
                );
                facts.Add(Fact.Create(FactPaths.SystemUptimeSeconds, [deviceId], (long)uptimeSecs));
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Kernel version and build
        string kernel = (await CollectorHelper.RunAsync("sysctl", "-n kern.osrelease", ct)).Trim();
        string build = (await CollectorHelper.RunAsync("sysctl", "-n kern.osversion", ct)).Trim();
        if (kernel != "")
        {
            facts.Add(Fact.Create(FactPaths.SystemKernel, [deviceId], kernel));
        }

        if (build != "")
        {
            facts.Add(Fact.Create(FactPaths.SystemOsBuild, [deviceId], build));
        }

        // macOS product name and version from sw_vers
        string swvers = await CollectorHelper.RunAsync("sw_vers", "", ct);
        Dictionary<string, string> kv = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in swvers.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            kv[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (kv.TryGetValue("ProductName", out string? distro))
        {
            facts.Add(Fact.Create(FactPaths.SystemOsDistro, [deviceId], distro));
        }

        if (kv.TryGetValue("ProductVersion", out string? version))
        {
            facts.Add(Fact.Create(FactPaths.SystemOsVersion, [deviceId], version));
        }

        // Boot time from kern.boottime: "{ sec = 1700000000, usec = 123 } ..."
        string bootStr = (await CollectorHelper.RunAsync("sysctl", "-n kern.boottime", ct)).Trim();
        int secIdx = bootStr.IndexOf("sec = ", StringComparison.Ordinal);
        if (secIdx >= 0)
        {
            string rest = bootStr[(secIdx + 6)..];
            int end = rest.IndexOfAny([',', ' ', '}']);
            if (end > 0 && long.TryParse(rest[..end].Trim(), out long sec))
            {
                DateTimeOffset bootTime = DateTimeOffset.FromUnixTimeSeconds(sec);
                long uptimeSecs = (long)(DateTimeOffset.UtcNow - bootTime).TotalSeconds;
                facts.Add(Fact.Create(FactPaths.SystemBootTime, [deviceId], bootTime.ToString("o")));
                facts.Add(Fact.Create(FactPaths.SystemUptimeSeconds, [deviceId], uptimeSecs));
            }
        }

        // Install date: birth time of /var/db/.AppleSetupDone
        try
        {
            FileInfo fi = new("/var/db/.AppleSetupDone");
            if (fi.Exists)
            {
                facts.Add(Fact.Create(FactPaths.SystemInstallDate, [deviceId], fi.LastWriteTimeUtc.ToString("o")));
            }
        }
        catch { }
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
            Get-CimInstance Win32_OperatingSystem |
            Select-Object Caption, Version, BuildNumber,
              @{n='InstallDate';e={$_.InstallDate.ToString('o')}},
              @{n='LastBootUpTime';e={$_.LastBootUpTime.ToString('o')}},
              OSArchitecture |
            ConvertTo-Json -Compress
            """;

        List<WinOsData> rows = await CollectorHelper.RunPsJsonAsync<WinOsData>(script, ct);
        WinOsData? d = rows.FirstOrDefault();
        if (d is null)
        {
            return;
        }

        void Add(string? val, string attributePath)
        {
            facts.AddIfPresent(attributePath, [deviceId], val);
        }

        Add(d.Caption?.Trim(), FactPaths.SystemOsDistro);
        Add(d.Version, FactPaths.SystemOsVersion);
        Add(d.Version, FactPaths.SystemKernel); // Kernel on Windows = NT version string
        Add(d.BuildNumber, FactPaths.SystemOsBuild);
        Add(d.OSArchitecture, FactPaths.SystemKernelArch);

        if (d.LastBootUpTime is { Length: > 0 } bt && DateTimeOffset.TryParse(bt, out DateTimeOffset bootTime))
        {
            facts.Add(Fact.Create(FactPaths.SystemBootTime, [deviceId], bootTime.ToString("o")));
            facts.Add(
                Fact.Create(
                    FactPaths.SystemUptimeSeconds,
                    [deviceId],
                    (long)(DateTimeOffset.UtcNow - bootTime).TotalSeconds
                )
            );
        }

        if (d.InstallDate is { Length: > 0 } id && DateTimeOffset.TryParse(id, out DateTimeOffset installDate))
        {
            facts.Add(Fact.Create(FactPaths.SystemInstallDate, [deviceId], installDate.ToString("o")));
        }
    }

    private sealed class WinOsData
    {
        public string? Caption { get; set; }
        public string? Version { get; set; }
        public string? BuildNumber { get; set; }
        public string? InstallDate { get; set; }
        public string? LastBootUpTime { get; set; }
        public string? OSArchitecture { get; set; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DetectFamily() =>
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "darwin" : "unknown";
}