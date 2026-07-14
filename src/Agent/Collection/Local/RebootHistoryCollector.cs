using System.Globalization;
using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

public sealed class RebootHistoryCollector : OsDispatchLocalCollector
{
    public override string Name => "reboot-history";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync("last", "reboot -F", ct);
            List<DateTimeOffset> boots = ParseLastOutput(output);
            EmitBootFacts(deviceId, facts, boots);
        }
        catch
        {
            await FallbackLinuxUptimeAsync(deviceId, facts, ct);
        }
    }

    private static async Task FallbackLinuxUptimeAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string uptime = await File.ReadAllTextAsync("/proc/uptime", ct);
            string[] parts = uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0
             && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
            {
                DateTimeOffset lastBoot = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(seconds);
                EmitBootFacts(
                    deviceId,
                    facts,
                    new List<DateTimeOffset>
                    {
                        lastBoot,
                    }
                );
            }
        }
        catch { }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync("last", "reboot", ct);
            List<DateTimeOffset> boots = ParseLastOutput(output);
            if (boots.Count > 0)
            {
                EmitBootFacts(deviceId, facts, boots);
                return;
            }
        }
        catch { }

        await FallbackMacOsSysctlAsync(deviceId, facts, ct);
    }

    private static async Task FallbackMacOsSysctlAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync("sysctl", "kern.boottime", ct);
            // kern.boottime: { sec = 1704196800, usec = 0 } Mon Jan  1 12:00:00 2024
            int secIdx = output.IndexOf("sec = ", StringComparison.Ordinal);
            if (secIdx >= 0)
            {
                string remainder = output[(secIdx + 6)..];
                int commaIdx = remainder.IndexOf(',');
                string secStr = commaIdx >= 0
                    ? remainder[..commaIdx].Trim()
                    : remainder.Split([' ', '}'], StringSplitOptions.RemoveEmptyEntries)[0];
                if (long.TryParse(secStr, out long epochSec))
                {
                    DateTimeOffset lastBoot = DateTimeOffset.FromUnixTimeSeconds(epochSec);
                    EmitBootFacts(
                        deviceId,
                        facts,
                        new List<DateTimeOffset>
                        {
                            lastBoot,
                        }
                    );
                }
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
        try
        {
            const string script = """
                Get-WinEvent -FilterHashtable @{LogName='System';Id=6005} -MaxEvents 50 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated | ConvertTo-Json -Compress
                """;

            List<WinEvent> events = await CollectorHelper.RunPsJsonAsync<WinEvent>(script, ct);

            List<DateTimeOffset> boots = new();
            foreach (WinEvent e in events)
            {
                if (e.TimeCreated is { } dto)
                {
                    boots.Add(dto.ToUniversalTime());
                }
            }

            boots.Sort(static (a, b) => b.CompareTo(a));
            EmitBootFacts(deviceId, facts, boots);
        }
        catch { }
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private static readonly string[] LastFormats =
    [
        "ddd MMM  d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    ];

    private static List<DateTimeOffset> ParseLastOutput(string output)
    {
        List<DateTimeOffset> boots = new();

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("reboot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("wtmp begins", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Columns 4-8: dow mon day HH:MM:SS YYYY
            // "reboot   system boot  5.15.0-91       Tue Jan  2 12:00:00 2024 ..."
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9)
            {
                continue;
            }

            // Reassemble the timestamp from parts[4..8] (5 tokens: dow mon day time year)
            string candidate = $"{parts[4]} {parts[5]} {parts[6]} {parts[7]} {parts[8]}";

            if (DateTime.TryParseExact(
                candidate,
                LastFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime dt
            ))
            {
                boots.Add(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime()));
            }
        }

        boots.Sort(static (a, b) => b.CompareTo(a));
        return boots;
    }

    private static void EmitBootFacts(string deviceId, List<Fact> facts, List<DateTimeOffset> boots)
    {
        if (boots.Count == 0)
        {
            return;
        }

        facts.Add(Fact.Create(FactPaths.RebootsLastBoot, [deviceId], boots[0]));

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        long count30d = boots.Count(b => b >= cutoff);
        facts.Add(Fact.Create(FactPaths.RebootsCount30d, [deviceId], count30d));

        int limit = Math.Min(boots.Count, 20);
        for (int n = 0; n < limit; n++)
        {
            facts.Add(Fact.Create(FactPaths.RebootBootTime, [deviceId, n.ToString()], boots[n]));
        }
    }

    private sealed class WinEvent
    {
        public DateTimeOffset? TimeCreated { get; set; }
    }
}