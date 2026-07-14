using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects battery facts: design/current capacity, cycle count, state, charge %.
/// Linux:   /sys/class/power_supply/BAT* or battery* sysfs nodes.
/// macOS:   pmset -g batt (charge/state); system_profiler SPPowerDataType -json skipped
/// (mAh not Wh — capacity fields emitted only when Wh is available).
/// Windows: Win32_Battery + BatteryStaticData WMI objects via PowerShell.
/// </summary>
public sealed class BatteryCollector : OsDispatchLocalCollector
{
    private static readonly char[] BatteryTokenSeparators = ['\t', ';', ' '];

    public override string Name => "battery";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override Task CollectLinuxAsync(string deviceId, List<Fact> facts, CancellationToken ct)
    {
        try
        {
            string psDir = "/sys/class/power_supply";
            if (!Directory.Exists(psDir))
            {
                return Task.CompletedTask;
            }

            foreach (string dir in Directory.EnumerateDirectories(psDir))
            {
                // Path.GetFileName never returns null for the non-null dir from
                // EnumerateDirectories, so no null check is needed.
                string name = Path.GetFileName(dir);

                // Only battery entries (BAT0, BAT1, battery, etc.)
                if (!name.StartsWith("BAT", StringComparison.OrdinalIgnoreCase)
                 && !name.StartsWith("battery", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] keys = [deviceId];

                // Prefer energy_full_design (μWh → Wh) over charge_full_design
                double? designWh = ReadWhFromEnergy(dir, "energy_full_design");
                double? currentWh = ReadWhFromEnergy(dir, "energy_full");

                if (designWh.HasValue)
                {
                    facts.Add(Fact.Create(FactPaths.BatteryDesignCapWh, keys, designWh.Value));
                }

                if (currentWh.HasValue)
                {
                    facts.Add(Fact.Create(FactPaths.BatteryCurrentCapWh, keys, currentWh.Value));
                }

                // Cycle count
                if (TryReadInt(dir, "cycle_count", out int cycles))
                {
                    facts.Add(Fact.Create(FactPaths.BatteryCycleCount, keys, cycles));
                }

                // State string (Charging/Discharging/Full/Unknown)
                facts.AddIfPresent(FactPaths.BatteryState, keys, TryReadText(dir, "status"));

                // Charge percent (capacity file)
                if (TryReadInt(dir, "capacity", out int pct))
                {
                    facts.Add(Fact.Create(FactPaths.BatteryChargePercent, keys, (double)pct));
                }

                // Only handle the first battery found
                break;
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads μWh from energy_* sysfs file and converts to Wh.
    /// Returns null if the file doesn't exist or can't be parsed.
    /// </summary>
    private static double? ReadWhFromEnergy(string dir, string file)
    {
        try
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            if (!long.TryParse(text, out long uWh))
            {
                return null;
            }

            return uWh / 1_000_000.0;
        }
        catch { return null; }
    }

    private static bool TryReadInt(string dir, string file, out int value)
    {
        value = 0;
        try
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                return false;
            }

            return int.TryParse(File.ReadAllText(path).Trim(), out value);
        }
        catch { return false; }
    }

    private static string? TryReadText(string dir, string file)
    {
        try
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                return null;
            }

            string s = File.ReadAllText(path).Trim();
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    protected override async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // pmset -g batt gives charge% and state without needing IOKit
        // Example line:
        //  -InternalBattery-0 (id=...)	79%; discharging; 3:42 remaining present: true
        try
        {
            string output = await CollectorHelper.RunAsync("pmset", "-g batt", ct);
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            string[] keys = [deviceId];

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                // Battery data lines contain a semicolon-separated status block
                if (!trimmed.Contains("%;"))
                {
                    continue;
                }

                // Extract percentage: find the token ending with %
                string[] tokens = trimmed.Split(
                    BatteryTokenSeparators,
                    StringSplitOptions.RemoveEmptyEntries
                );

                string? pctToken = null;
                string? stateToken = null;

                foreach (string tok in tokens)
                {
                    if (tok.EndsWith('%') && pctToken is null)
                    {
                        pctToken = tok.TrimEnd('%');
                    }
                    else if (tok is "charging" or "discharging" or "charged")
                    {
                        stateToken = tok;
                    }
                }

                // "finishing charge" is two words, so it can never appear as a single
                // space-split token above — detect it from the line directly.
                if (stateToken is null
                 && trimmed.Contains("finishing charge", StringComparison.OrdinalIgnoreCase))
                {
                    stateToken = "finishing charge";
                }

                if (pctToken is not null
                 && double.TryParse(
                        pctToken,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double pct
                    ))
                {
                    facts.Add(Fact.Create(FactPaths.BatteryChargePercent, keys, pct));
                }

                if (stateToken is not null)
                {
                    string state = stateToken switch
                    {
                        "charging" => "Charging",
                        "discharging" => "Discharging",
                        "charged" => "Full",
                        "finishing charge" => "Charging",
                        _ => stateToken,
                    };
                    facts.Add(Fact.Create(FactPaths.BatteryState, keys, state));
                }

                // Only parse one battery entry
                break;
            }

            // Cycle count via system_profiler (best-effort; skip if unavailable)
            string spJson = await CollectorHelper.RunAsync("system_profiler", "SPPowerDataType -json", ct);
            if (!string.IsNullOrWhiteSpace(spJson))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(spJson);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("SPPowerDataType", out JsonElement arr) && arr.GetArrayLength() > 0)
                    {
                        JsonElement entry = arr[0];
                        if (entry.TryGetProperty("sppower_battery_model_info", out JsonElement info)
                         && info.TryGetProperty("sppower_battery_cycle_count", out JsonElement cyc)
                         && cyc.TryGetInt32(out int cycleCount))
                        {
                            facts.Add(Fact.Create(FactPaths.BatteryCycleCount, keys, cycleCount));
                        }
                    }
                }
                catch { }
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
        // Win32_Battery gives charge% and status.
        // BatteryStaticData (root/WMI) gives design capacity and cycle count.
        // Both are optional — the script emits nothing if no battery is found.
        const string script = """
            $b = Get-WmiObject -Class Win32_Battery -ErrorAction SilentlyContinue
            $s = Get-WmiObject -Class BatteryStaticData -Namespace root/WMI -ErrorAction SilentlyContinue
            if ($b) {
                [pscustomobject]@{
                    DesignCapacity = if ($s) { $s.DesignedCapacity } else { 0 }
                    FullCharge     = $b.FullChargeCapacity
                    CycleCount     = if ($s) { $s.CycleCount } else { 0 }
                    Status         = $b.BatteryStatus
                    ChargePercent  = $b.EstimatedChargeRemaining
                } | ConvertTo-Json -Compress
            }
            """;

        WindowsBatteryRow? row = await CollectorHelper.RunPsJsonOneAsync<WindowsBatteryRow>(script, ct);
        if (row is null)
        {
            return;
        }

        string[] keys = [deviceId];

        // DesignedCapacity and FullChargeCapacity from WMI are in mWh → convert to Wh
        if (row.DesignCapacity > 0)
        {
            facts.Add(Fact.Create(FactPaths.BatteryDesignCapWh, keys, row.DesignCapacity / 1000.0));
        }

        if (row.FullCharge > 0)
        {
            facts.Add(Fact.Create(FactPaths.BatteryCurrentCapWh, keys, row.FullCharge / 1000.0));
        }

        if (row.CycleCount > 0)
        {
            facts.Add(Fact.Create(FactPaths.BatteryCycleCount, keys, row.CycleCount));
        }

        // Map Win32_Battery BatteryStatus integer to string
        string state = row.Status switch
        {
            2 => "Unknown",
            3 => "Full",
            4 => "Low",
            5 => "Critical",
            6 => "Charging",
            11 => "Charging", // Partially Charged
            _ => "Discharging",
        };
        facts.Add(Fact.Create(FactPaths.BatteryState, keys, state));
        facts.Add(Fact.Create(FactPaths.BatteryChargePercent, keys, (double)row.ChargePercent));
    }

    private sealed class WindowsBatteryRow
    {
        public int DesignCapacity { get; set; }
        public int FullCharge { get; set; }
        public int CycleCount { get; set; }
        public int Status { get; set; }
        public int ChargePercent { get; set; }
    }
}