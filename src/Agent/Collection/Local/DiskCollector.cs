using System.Runtime.Versioning;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects block device facts and enriches with SMART health data.
/// Linux:   /sys/block + smartctl -j -a.
/// macOS:   diskutil list + diskutil info + smartctl (if installed).
/// Windows: PowerShell Get-PhysicalDisk + Get-StorageReliabilityCounter.
/// </summary>
public sealed class DiskCollector : OsDispatchLocalCollector
{
    public override string Name => "disk";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        if (!Directory.Exists("/sys/block"))
        {
            return;
        }

        bool smartAvailable = CollectorHelper.BinaryExists("smartctl");

        foreach (string entry in Directory.EnumerateDirectories("/sys/block"))
        {
            string name = Path.GetFileName(entry);
            if (name.StartsWith("loop", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("ram", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("dm-", StringComparison.OrdinalIgnoreCase)
             || name.StartsWith("md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] diskKeys = [deviceId, name];
            facts.Add(Fact.Create(FactPaths.DiskName, diskKeys, name));
            facts.Add(Fact.Create(FactPaths.DiskModel, diskKeys, TryRead($"{entry}/device/model")));
            facts.Add(Fact.Create(FactPaths.DiskSerial, diskKeys, TryRead($"{entry}/device/serial")));
            facts.Add(Fact.Create(FactPaths.DiskRemovable, diskKeys, TryRead($"{entry}/removable") == "1"));

            if (ulong.TryParse(TryRead($"{entry}/size"), out ulong sectors))
            {
                facts.Add(Fact.Create(FactPaths.DiskSizeBytes, diskKeys, (long)(sectors * 512)));
            }

            string rot = TryRead($"{entry}/queue/rotational");
            facts.Add(
                Fact.Create(
                    FactPaths.DiskType,
                    diskKeys,
                    name.StartsWith("nvme", StringComparison.OrdinalIgnoreCase) ? "nvme" :
                    rot == "0" ? "ssd" :
                    rot == "1" ? "hdd" : "unknown"
                )
            );

            if (smartAvailable)
            {
                await EnrichLinuxSmartAsync(diskKeys, name, facts, ct);
            }
        }
    }

    private static async Task EnrichLinuxSmartAsync(
        string[] keys,
        string devName,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        string json = await CollectorHelper.RunAsync("smartctl", $"-j -a /dev/{devName}", ct, timeoutSeconds: 30);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("smart_status", out JsonElement s) && s.TryGetProperty("passed", out JsonElement p))
            {
                facts.Add(Fact.Create(FactPaths.DiskSmartOverallHealth, keys, p.GetBoolean() ? "PASSED" : "FAILED"));
            }

            if (root.TryGetProperty("temperature", out JsonElement t) && t.TryGetProperty("current", out JsonElement c))
            {
                facts.Add(Fact.Create(FactPaths.DiskSmartTempC, keys, c.GetDouble()));
            }

            if (root.TryGetProperty("power_on_time", out JsonElement pot)
             && pot.TryGetProperty("hours", out JsonElement h))
            {
                facts.Add(Fact.Create(FactPaths.DiskSmartPowerOnHours, keys, h.GetInt64()));
            }

            if (root.TryGetProperty("power_cycle_count", out JsonElement pcc))
            {
                facts.Add(Fact.Create(FactPaths.DiskSmartPowerCycles, keys, pcc.GetInt64()));
            }

            if (root.TryGetProperty("nvme_smart_health_information_log", out JsonElement nvme))
            {
                if (nvme.TryGetProperty("percentage_used", out JsonElement pu))
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartPercentageUsed, keys, pu.GetDouble()));
                }

                if (nvme.TryGetProperty("available_spare", out JsonElement sp2))
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartAvailableSparePct, keys, sp2.GetDouble()));
                }

                if (nvme.TryGetProperty("data_units_read", out JsonElement dur))
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartDataReadGB, keys, dur.GetDouble() * 512 * 1000 / 1e9));
                }

                if (nvme.TryGetProperty("data_units_written", out JsonElement duw))
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartDataWrittenGB, keys, duw.GetDouble() * 512 * 1000 / 1e9));
                }
            }

            if (root.TryGetProperty("ata_smart_attributes", out JsonElement ata)
             && ata.TryGetProperty("table", out JsonElement table))
            {
                foreach (JsonElement attr in table.EnumerateArray())
                {
                    if (!attr.TryGetProperty("id", out JsonElement id)
                     || !attr.TryGetProperty("raw", out JsonElement raw)
                     || !raw.TryGetProperty("value", out JsonElement rv))
                    {
                        continue;
                    }

                    switch (id.GetInt32())
                    {
                        case 5: facts.Add(Fact.Create(FactPaths.DiskSmartReallocSectors, keys, rv.GetInt64())); break;
                        case 197: facts.Add(Fact.Create(FactPaths.DiskSmartPendingSectors, keys, rv.GetInt64())); break;
                        case 198: facts.Add(Fact.Create(FactPaths.DiskSmartUncorrErrors, keys, rv.GetInt64())); break;
                        case 199: facts.Add(Fact.Create(FactPaths.DiskSmartCrcErrors, keys, rv.GetInt64())); break;
                        case 231 or 233:
                            if (attr.TryGetProperty("value", out JsonElement pct) && pct.GetInt32() is > 0 and <= 100)
                            {
                                facts.Add(
                                    Fact.Create(FactPaths.DiskSmartWearPercent, keys, (double)(100 - pct.GetInt32()))
                                );
                            }

                            break;
                    }
                }
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
        // diskutil list gives us disk names; diskutil info gives per-disk details.
        string listOut = await CollectorHelper.RunAsync("diskutil", "list", ct);
        List<string> disks = new();

        foreach (string line in listOut.Split('\n'))
        {
            // Whole-disk header lines look like "/dev/disk0 (internal, physical):".
            // Partition rows are indented and don't start with /dev/, so this only
            // ever matches whole disks. Restrict to physical media so we skip
            // synthesized APFS containers (e.g. disk3) and disk images.
            if (!line.StartsWith("/dev/disk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!line.Contains("physical", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            disks.Add(line.Split(' ')[0]["/dev/".Length..]);
        }

        foreach (string disk in disks.Distinct())
        {
            string infoOut = await CollectorHelper.RunAsync("diskutil", $"info /dev/{disk}", ct);
            Dictionary<string, string> kv = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in infoOut.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                kv[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            string[] diskKeys = [deviceId, disk];
            facts.Add(Fact.Create(FactPaths.DiskName, diskKeys, disk));
            facts.Add(Fact.Create(FactPaths.DiskModel, diskKeys, kv.GetValueOrDefault("Device / Media Name", "")));
            facts.Add(
                Fact.Create(
                    FactPaths.DiskType,
                    diskKeys,
                    kv.GetValueOrDefault("Solid State", "No") == "Yes" ? "ssd" : "hdd"
                )
            );

            // "Disk Size: 2.0 TB (2001111162880 Bytes) (exactly ...)" → exact bytes.
            if (kv.TryGetValue("Disk Size", out string? sizeStr))
            {
                long bytes = ParseBytesInParens(sizeStr);
                if (bytes > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DiskSizeBytes, diskKeys, bytes));
                }
            }

            // diskutil reports "Verified" / "Not Supported" / "Failing" etc.
            if (kv.TryGetValue("SMART Status", out string? smart)
             && !string.IsNullOrWhiteSpace(smart)
             && !smart.Equals("Not Supported", StringComparison.OrdinalIgnoreCase))
            {
                string health = smart.Equals("Verified", StringComparison.OrdinalIgnoreCase) ? "PASSED" : smart;
                facts.Add(Fact.Create(FactPaths.DiskSmartOverallHealth, diskKeys, health));
            }
        }
    }

    // Extracts the byte count from a diskutil size string such as
    // "2.0 TB (2001111162880 Bytes) (exactly 3908420240 512-Byte-Units)".
    private static long ParseBytesInParens(string sizeStr)
    {
        int open = sizeStr.IndexOf('(');
        if (open < 0)
        {
            return 0;
        }

        int i = open + 1;
        while (i < sizeStr.Length && !char.IsDigit(sizeStr[i]))
        {
            i++;
        }

        int start = i;
        while (i < sizeStr.Length && char.IsDigit(sizeStr[i]))
        {
            i++;
        }

        return start < i && long.TryParse(sizeStr[start..i], out long bytes) ? bytes : 0;
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Try Get-PhysicalDisk first (requires Storage module); fall back to Win32_DiskDrive.
        const string primaryScript = """
            Get-PhysicalDisk |
            Select-Object FriendlyName, SerialNumber, Size, MediaType, BusType, DeviceId |
            ConvertTo-Json -Compress
            """;

        List<PhysicalDiskRow> rows = await CollectorHelper.RunPsJsonAsync<PhysicalDiskRow>(primaryScript, ct);

        if (rows.Count == 0)
        {
            const string fallbackScript = """
                Get-CimInstance Win32_DiskDrive |
                Select-Object Caption, SerialNumber, Size, Index |
                ConvertTo-Json -Compress
                """;
            List<DiskDriveRow> fb = await CollectorHelper.RunPsJsonAsync<DiskDriveRow>(fallbackScript, ct);
            foreach (DiskDriveRow r in fb)
            {
                string name = $"PhysicalDrive{r.Index}";
                string[] diskKeys = [deviceId, name];
                facts.Add(Fact.Create(FactPaths.DiskName, diskKeys, name));
                facts.Add(Fact.Create(FactPaths.DiskModel, diskKeys, r.Caption?.Trim() ?? ""));
                facts.Add(Fact.Create(FactPaths.DiskSerial, diskKeys, r.SerialNumber?.Trim() ?? ""));
                facts.Add(Fact.Create(FactPaths.DiskSizeBytes, diskKeys, (long)(r.Size ?? 0)));
                facts.Add(Fact.Create(FactPaths.DiskType, diskKeys, "unknown"));
            }

            return;
        }

        // Enrich with SMART via Get-StorageReliabilityCounter
        Dictionary<string, SmartRow> smartRows = await CollectWindowsSmartAsync(ct);

        foreach (PhysicalDiskRow r in rows)
        {
            string name = r.DeviceId ?? r.FriendlyName ?? "unknown";
            string[] diskKeys = [deviceId, name];
            string busType = r.BusType?.ToLowerInvariant() ?? "";
            string mediaType = r.MediaType?.ToLowerInvariant() switch
            {
                "ssd" or "solid state drive" => "ssd",
                "hdd" or "hard disk drive" => "hdd",
                "scm" => "nvme",
                _ when busType == "nvme" => "nvme",
                _ => "unknown",
            };

            facts.Add(Fact.Create(FactPaths.DiskName, diskKeys, name));
            facts.Add(Fact.Create(FactPaths.DiskModel, diskKeys, r.FriendlyName?.Trim() ?? ""));
            facts.Add(Fact.Create(FactPaths.DiskSerial, diskKeys, r.SerialNumber?.Trim() ?? ""));
            facts.Add(Fact.Create(FactPaths.DiskSizeBytes, diskKeys, (long)(r.Size ?? 0)));
            facts.Add(Fact.Create(FactPaths.DiskType, diskKeys, mediaType));

            string serial = r.SerialNumber?.Trim() ?? "";
            if (serial != "" && smartRows.TryGetValue(serial, out SmartRow? smart))
            {
                facts.Add(Fact.Create(FactPaths.DiskSmartOverallHealth, diskKeys, smart.HealthStatus ?? "UNKNOWN"));
                if (smart.Temperature > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartTempC, diskKeys, smart.Temperature));
                }

                if (smart.PowerOnHours > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartPowerOnHours, diskKeys, (long)smart.PowerOnHours));
                }

                if (smart.Wear > 0)
                {
                    facts.Add(Fact.Create(FactPaths.DiskSmartWearPercent, diskKeys, (double)smart.Wear));
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<Dictionary<string, SmartRow>> CollectWindowsSmartAsync(CancellationToken ct)
    {
        const string script = """
            Get-PhysicalDisk | ForEach-Object {
              $r = $null; try { $r = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
              [pscustomobject]@{
                SerialNumber  = $_.SerialNumber
                HealthStatus  = $_.HealthStatus
                Wear          = if ($r) { $r.Wear }         else { 0 }
                Temperature   = if ($r) { $r.Temperature }  else { 0 }
                PowerOnHours  = if ($r) { $r.PowerOnHours } else { 0 }
              }
            } | ConvertTo-Json -Compress -Depth 3
            """;
        List<SmartRow> rows = await CollectorHelper.RunPsJsonAsync<SmartRow>(script, ct);
        Dictionary<string, SmartRow> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (SmartRow r in rows)
        {
            if (r.SerialNumber is { Length: > 0 } s)
            {
                dict[s.Trim()] = r;
            }
        }

        return dict;
    }

    private sealed class PhysicalDiskRow
    {
        public string? FriendlyName { get; set; }
        public string? SerialNumber { get; set; }
        public ulong? Size { get; set; }
        public string? MediaType { get; set; }
        public string? BusType { get; set; }
        public string? DeviceId { get; set; }
    }

    private sealed class DiskDriveRow
    {
        public string? Caption { get; set; }
        public string? SerialNumber { get; set; }
        public ulong? Size { get; set; }
        public int Index { get; set; }
    }

    private sealed class SmartRow
    {
        public string? SerialNumber { get; set; }
        public string? HealthStatus { get; set; }
        public int Wear { get; set; }
        public double Temperature { get; set; }
        public ulong PowerOnHours { get; set; }
    }

    private static string TryRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return ""; }
    }
}