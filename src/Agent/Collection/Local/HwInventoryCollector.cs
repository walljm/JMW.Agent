using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects hardware component inventory facts: DIMMs, CPUs, PCIe devices, fans,
/// temperature sensors, etc.
/// Linux:   dmidecode -q (memory + CPUs), /sys/class/hwmon (fans/temp), lspci (PCIe).
/// macOS:   system_profiler SPMemoryDataType / SPPCIDataType (JSON).
/// Windows: Get-CimInstance Win32_PhysicalMemory + Win32_Processor via PowerShell.
/// All failures are non-fatal — missing tools or access-denied return partial results.
/// </summary>
public sealed class HwInventoryCollector : ILocalCollector
{
    public string Name => "hw-inventory";
    public bool IsSupported => true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        if (OperatingSystem.IsLinux())
        {
            await CollectLinuxAsync(deviceId, facts, ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await CollectMacOsAsync(deviceId, facts, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            await CollectWindowsAsync(deviceId, facts, ct);
        }

        return facts;
    }

    // ── Shared emit helper ────────────────────────────────────────────────────

    private static void EmitComponent(
        string deviceId,
        List<Fact> facts,
        string key,
        string cls,
        string? slot,
        string? description,
        string? vendor,
        string? model,
        string? serial,
        string? firmware,
        string status,
        bool isFru,
        object? details
    )
    {
        string[] keys = [deviceId, key];

        facts.Add(Fact.Create(FactPaths.HwComponentClass, keys, cls));
        facts.AddIfPresent(FactPaths.HwComponentSlot, keys, slot);
        facts.AddIfPresent(FactPaths.HwComponentDescription, keys, description);
        facts.AddIfPresent(FactPaths.HwComponentVendor, keys, vendor);
        facts.AddIfPresent(FactPaths.HwComponentModel, keys, model);
        facts.AddIfPresent(FactPaths.HwComponentSerial, keys, serial);
        facts.AddIfPresent(FactPaths.HwComponentFirmware, keys, firmware);
        facts.Add(Fact.Create(FactPaths.HwComponentStatus, keys, status));
        facts.Add(Fact.Create(FactPaths.HwComponentIsFru, keys, isFru));
        if (details is not null)
        {
            try { facts.Add(Fact.Create(FactPaths.HwComponentDetails, keys, JsonSerializer.Serialize(details))); }
            catch { }
        }
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    private static async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        await CollectDmidecodeAsync(deviceId, facts, ct);
        CollectHwmonFansAndTemps(deviceId, facts);
        await CollectLspciAsync(deviceId, facts, ct);
    }

    // Memory devices and processors via dmidecode -q (requires sudo -n / passwordless sudo).
    // One invocation covers every DMI structure; scalar system/board/BIOS/chassis identity is
    // consumed separately by HardwareCollector.
    private static async Task CollectDmidecodeAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        IReadOnlyList<DmiDecode.Section> sections = await DmiDecode.RunAsync(ct);
        foreach (DmiDecode.Section s in sections)
        {
            if (s.Name.Equals("Memory Device", StringComparison.OrdinalIgnoreCase))
            {
                EmitMemoryDevice(deviceId, facts, s.Fields);
            }
            else if (s.Name.Equals("Processor Information", StringComparison.OrdinalIgnoreCase))
            {
                EmitProcessor(deviceId, facts, s.Fields);
            }
        }
    }

    private static void EmitMemoryDevice(
        string deviceId,
        List<Fact> facts,
        IReadOnlyDictionary<string, string> fields
    )
    {
        if (!fields.TryGetValue("Locator", out string? locator))
        {
            return;
        }

        if (!fields.TryGetValue("Size", out string? sizeStr) || sizeStr == "No Module Installed")
        {
            return;
        }

        // Parse size: "8192 MB" or "16 GB"
        long sizeBytes = 0;
        string[] sizeParts = sizeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sizeParts.Length >= 2 && long.TryParse(sizeParts[0], out long sizeNum))
        {
            sizeBytes = sizeParts[1].ToUpperInvariant() switch
            {
                "GB" => sizeNum * 1024L * 1024L * 1024L,
                "MB" => sizeNum * 1024L * 1024L,
                "KB" => sizeNum * 1024L,
                _ => sizeNum,
            };
        }

        // Parse speed: "3200 MT/s" or "3200 MHz"
        fields.TryGetValue("Speed", out string? speedStr);
        int speedMhz = ParseLeadingInt(speedStr);

        fields.TryGetValue("Manufacturer", out string? mfr);
        fields.TryGetValue("Part Number", out string? part);
        fields.TryGetValue("Serial Number", out string? ser);
        fields.TryGetValue("Type", out string? memType);
        fields.TryGetValue("Form Factor", out string? formFactor);

        var details = new
        {
            size_bytes = sizeBytes,
            speed_mhz = speedMhz,
            memory_type = memType ?? "",
            form_factor = formFactor ?? "",
        };

        EmitComponent(
            deviceId,
            facts,
            key: locator,
            cls: "memory",
            slot: locator,
            description: $"{mfr} {part}".Trim(),
            vendor: mfr?.Trim(),
            model: part?.Trim(),
            serial: ser?.Trim(),
            firmware: null,
            status: "ok",
            isFru: true,
            details: details
        );
    }

    private static void EmitProcessor(
        string deviceId,
        List<Fact> facts,
        IReadOnlyDictionary<string, string> fields
    )
    {
        string? socket = DmiDecode.Clean(fields.GetValueOrDefault("Socket Designation"));
        if (socket is null)
        {
            return;
        }

        // Skip empty sockets ("Status: Unpopulated").
        if (fields.TryGetValue("Status", out string? status)
         && status.Contains("Unpopulated", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? vendor = DmiDecode.Clean(fields.GetValueOrDefault("Manufacturer"));
        // "Version" holds the brand string ("Intel(R) Core(TM) i7-...") on populated sockets;
        // fall back to "Family" ("Xeon", "Core i7") when the OEM left Version unset.
        string? model = DmiDecode.Clean(fields.GetValueOrDefault("Version"))
         ?? DmiDecode.Clean(fields.GetValueOrDefault("Family"));
        string? serial = DmiDecode.Clean(fields.GetValueOrDefault("Serial Number"));

        var details = new
        {
            cores = ParseLeadingInt(fields.GetValueOrDefault("Core Count")),
            threads = ParseLeadingInt(fields.GetValueOrDefault("Thread Count")),
            speed_mhz = ParseLeadingInt(fields.GetValueOrDefault("Max Speed")),
            family = DmiDecode.Clean(fields.GetValueOrDefault("Family")) ?? "",
            signature = DmiDecode.Clean(fields.GetValueOrDefault("Signature")) ?? "",
        };

        EmitComponent(
            deviceId,
            facts,
            key: socket,
            cls: "cpu",
            slot: socket,
            description: model ?? socket,
            vendor: vendor,
            model: model,
            serial: serial,
            firmware: null,
            status: "ok",
            isFru: true,
            details: details
        );
    }

    // Parses the leading integer from strings like "3200 MT/s", "3600 MHz", or "8".
    private static int ParseLeadingInt(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 1 && int.TryParse(parts[0], out int n) ? n : 0;
    }

    // Fans and temperature sensors via hwmon
    private static void CollectHwmonFansAndTemps(string deviceId, List<Fact> facts)
    {
        try
        {
            string hwmonBase = "/sys/class/hwmon";
            if (!Directory.Exists(hwmonBase))
            {
                return;
            }

            foreach (string chip in Directory.EnumerateDirectories(hwmonBase))
            {
                string chipName;
                try { chipName = File.ReadAllText(Path.Combine(chip, "name")).Trim(); }
                catch { chipName = Path.GetFileName(chip); }

                // Fans
                for (int n = 1; n <= 32; n++)
                {
                    string inputPath = Path.Combine(chip, $"fan{n}_input");
                    if (!File.Exists(inputPath))
                    {
                        break;
                    }

                    if (!long.TryParse(File.ReadAllText(inputPath).Trim(), out long rpm))
                    {
                        continue;
                    }

                    long threshold = 0;
                    string minPath = Path.Combine(chip, $"fan{n}_min");
                    if (File.Exists(minPath))
                    {
                        _ = long.TryParse(File.ReadAllText(minPath).Trim(), out threshold);
                    }

                    string key = $"{chipName}:fan{n}";
                    string status = threshold > 0 && rpm <= threshold ? "failed" : "ok";
                    var details = new
                    {
                        rpm,
                        rpm_low_threshold = threshold,
                    };

                    EmitComponent(
                        deviceId,
                        facts,
                        key: key,
                        cls: "fan",
                        slot: key,
                        description: $"{chipName} Fan {n}",
                        vendor: null,
                        model: null,
                        serial: null,
                        firmware: null,
                        status: status,
                        isFru: false,
                        details: details
                    );
                }

                // Temperature sensors
                for (int n = 1; n <= 32; n++)
                {
                    string inputPath = Path.Combine(chip, $"temp{n}_input");
                    if (!File.Exists(inputPath))
                    {
                        break;
                    }

                    if (!long.TryParse(File.ReadAllText(inputPath).Trim(), out long milliC))
                    {
                        continue;
                    }

                    double tempC = milliC / 1000.0;
                    if (tempC is <= 0 or > 200)
                    {
                        continue;
                    }

                    string label;
                    string labelPath = Path.Combine(chip, $"temp{n}_label");
                    try { label = File.Exists(labelPath) ? File.ReadAllText(labelPath).Trim() : $"temp{n}"; }
                    catch { label = $"temp{n}"; }

                    string key = $"{chipName}:{label}";
                    var details = new
                    {
                        temperature_c = tempC,
                    };

                    EmitComponent(
                        deviceId,
                        facts,
                        key: key,
                        cls: "sensor",
                        slot: key,
                        description: $"{chipName} {label}",
                        vendor: null,
                        model: null,
                        serial: null,
                        firmware: null,
                        status: "ok",
                        isFru: false,
                        details: details
                    );
                }
            }
        }
        catch { }
    }

    // PCIe devices via lspci -mm
    private static async Task CollectLspciAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            if (!CollectorHelper.BinaryExists("lspci"))
            {
                return;
            }

            string output = await CollectorHelper.RunAsync("lspci", "-mm", ct);
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // lspci -mm: quoted fields — slot "class" "vendor" "device" ...
                string[] parts = ParseLspciLine(line);
                if (parts.Length < 4)
                {
                    continue;
                }

                string slot = parts[0];
                string cls = MapPciClass(parts[1]);
                string vendor = parts[2];
                string device = parts[3];

                EmitComponent(
                    deviceId,
                    facts,
                    key: slot,
                    cls: cls,
                    slot: slot,
                    description: device,
                    vendor: vendor,
                    model: device,
                    serial: null,
                    firmware: null,
                    status: "ok",
                    isFru: false,
                    details: null
                );
            }
        }
        catch { }
    }

    private static string[] ParseLspciLine(string line)
    {
        // lspci -mm output is either space-separated or quote-delimited
        // Typical: 00:1f.2 "Mass storage controller" "Intel Corp." "...device..."
        List<string> results = new();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"')
            {
                i++; // skip opening quote
                int start = i;
                while (i < line.Length && line[i] != '"')
                {
                    i++;
                }

                results.Add(line[start..i]);
                if (i < line.Length)
                {
                    i++; // skip closing quote
                }
            }
            else
            {
                int start = i;
                while (i < line.Length && line[i] != ' ')
                {
                    i++;
                }

                results.Add(line[start..i]);
            }
        }

        return [.. results];
    }

    private static string MapPciClass(string pciClass)
    {
        string lower = pciClass.ToLowerInvariant();
        if (
            lower.Contains("network")
         || lower.Contains("ethernet")
        )
        {
            return "nic";
        }

        if (
            lower.Contains("storage")
         || lower.Contains("ide")
         || lower.Contains("sata")
         || lower.Contains("nvme")
        )
        {
            return "storage";
        }

        if (
            lower.Contains("usb")
         || lower.Contains("serial")
         || lower.Contains("audio")
         || lower.Contains("display")
         || lower.Contains("vga")
         || lower.Contains("gpu"))
        {
            return "other";
        }

        return "unknown";
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    private static async Task CollectMacOsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        await CollectMacOsMemoryAsync(deviceId, facts, ct);
        await CollectMacOsPciAsync(deviceId, facts, ct);
    }

    private static async Task CollectMacOsMemoryAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string json = await CollectorHelper.RunAsync("system_profiler", "SPMemoryDataType -json", ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPMemoryDataType", out JsonElement arr))
            {
                return;
            }

            if (arr.GetArrayLength() == 0)
            {
                return;
            }

            JsonElement entry = arr[0];
            if (!entry.TryGetProperty("_items", out JsonElement items))
            {
                return;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string slotName = item.GetStr("_name") ?? "";
                string dimSize = item.GetStr("dimm_size") ?? "";
                string dimSpeed = item.GetStr("dimm_speed") ?? "";
                string dimMfr = item.GetStr("dimm_manufacturer") ?? "";
                string dimPart = item.GetStr("dimm_part_number") ?? "";
                string dimType = item.GetStr("dimm_type") ?? "";

                if (slotName.Length == 0)
                {
                    continue;
                }

                // Parse size string like "8 GB" → bytes
                long sizeBytes = 0;
                string[] sp = dimSize.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length >= 2 && long.TryParse(sp[0], out long sNum))
                {
                    sizeBytes = sp[1].ToUpperInvariant() switch
                    {
                        "GB" => sNum * 1024L * 1024L * 1024L,
                        "MB" => sNum * 1024L * 1024L,
                        "TB" => sNum * 1024L * 1024L * 1024L * 1024L,
                        _ => sNum,
                    };
                }

                // Parse speed: "3200 MHz" or "LPDDR5"
                int speedMhz = 0;
                string[] spd = dimSpeed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (spd.Length >= 1)
                {
                    _ = int.TryParse(spd[0], out speedMhz);
                }

                var details = new
                {
                    size_bytes = sizeBytes,
                    speed_mhz = speedMhz,
                    memory_type = dimType,
                };

                EmitComponent(
                    deviceId,
                    facts,
                    key: slotName,
                    cls: "memory",
                    slot: slotName,
                    description: $"{dimMfr} {dimPart}".Trim(),
                    vendor: dimMfr.Length > 0 ? dimMfr : null,
                    model: dimPart.Length > 0 ? dimPart : null,
                    serial: null,
                    firmware: null,
                    status: "ok",
                    isFru: true,
                    details: details
                );
            }
        }
        catch { }
    }

    private static async Task CollectMacOsPciAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string json = await CollectorHelper.RunAsync("system_profiler", "SPPCIDataType -json", ct);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPPCIDataType", out JsonElement arr))
            {
                return;
            }

            foreach (JsonElement item in arr.EnumerateArray())
            {
                string name = item.GetStr("_name") ?? "";
                string vendor = item.GetStr("sppci_vendor") ?? "";
                string busId = item.GetStr("sppci_bus") ?? "";
                string devId = item.GetStr("sppci_device-id") ?? "";

                string key = busId.Length > 0 ? busId : name;
                if (key.Length == 0)
                {
                    continue;
                }

                EmitComponent(
                    deviceId,
                    facts,
                    key: key,
                    cls: "other",
                    slot: busId.Length > 0 ? busId : null,
                    description: name,
                    vendor: vendor.Length > 0 ? vendor : null,
                    model: name.Length > 0 ? name : null,
                    serial: null,
                    firmware: null,
                    status: "ok",
                    isFru: false,
                    details: devId.Length > 0
                        ? new
                        {
                            device_id = devId,
                        }
                        : null
                );
            }
        }
        catch { }
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Use Get-CimInstance (works on PS 5.1 and PS 7+, unlike Get-WmiObject
        // which was removed in PS 7). Avoid ?. null-conditional (PS 5.1 doesn't
        // support it) — use if/$null checks instead.
        const string script = """
            $results = @()
            Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue | ForEach-Object {
                $details = @{
                    size_bytes  = [long]$_.Capacity
                    speed_mhz   = [int]$_.Speed
                    memory_type = [int]$_.MemoryType
                    form_factor = [int]$_.FormFactor
                } | ConvertTo-Json -Compress
                $vendor = if ($_.Manufacturer) { $_.Manufacturer.Trim() } else { '' }
                $model  = if ($_.PartNumber)   { $_.PartNumber.Trim()   } else { '' }
                $serial = if ($_.SerialNumber) { $_.SerialNumber.Trim() } else { '' }
                $results += [pscustomobject]@{
                    Key         = $_.DeviceLocator
                    Class       = 'memory'
                    Slot        = $_.DeviceLocator
                    Description = "$vendor $model".Trim()
                    Vendor      = $vendor
                    Model       = $model
                    Serial      = $serial
                    IsFru       = $true
                    Status      = 'ok'
                    Details     = $details
                }
            }
            Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | ForEach-Object {
                $details = @{
                    cores   = [int]$_.NumberOfCores
                    threads = [int]$_.NumberOfLogicalProcessors
                    speed_mhz = [int]$_.MaxClockSpeed
                    socket  = $_.SocketDesignation
                } | ConvertTo-Json -Compress
                $vendor = if ($_.Manufacturer) { $_.Manufacturer.Trim() } else { '' }
                $model  = if ($_.Name)         { $_.Name.Trim()         } else { '' }
                $results += [pscustomobject]@{
                    Key         = $_.SocketDesignation
                    Class       = 'cpu'
                    Slot        = $_.SocketDesignation
                    Description = $model
                    Vendor      = $vendor
                    Model       = $model
                    Serial      = ''
                    IsFru       = $false
                    Status      = 'ok'
                    Details     = $details
                }
            }
            $results | ConvertTo-Json -Compress -Depth 3
            """;

        List<HwComponentRow> rows = await CollectorHelper.RunPsJsonAsync<HwComponentRow>(script, ct);
        foreach (HwComponentRow r in rows)
        {
            if (r.Key is null)
            {
                continue;
            }

            EmitComponent(
                deviceId,
                facts,
                key: r.Key,
                cls: r.Class ?? "other",
                slot: r.Slot,
                description: r.Description,
                vendor: r.Vendor,
                model: r.Model,
                serial: r.Serial,
                firmware: null,
                status: r.Status ?? "ok",
                isFru: r.IsFru,
                details: r.Details is { Length: > 0 }
                    ? JsonDocument.Parse(r.Details).RootElement
                    : null
            );
        }
    }

    private sealed class HwComponentRow
    {
        public string? Key { get; set; }
        public string? Class { get; set; }
        public string? Slot { get; set; }
        public string? Description { get; set; }
        public string? Vendor { get; set; }
        public string? Model { get; set; }
        public string? Serial { get; set; }
        public bool IsFru { get; set; }
        public string? Status { get; set; }

        [JsonPropertyName("Details")]
        public string? Details { get; set; }
    }
}