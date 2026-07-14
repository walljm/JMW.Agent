using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects hardware facts: CPU, memory, system identity, virtualization, temperatures.
/// Linux:   /proc/cpuinfo, /proc/meminfo, dmidecode -q (system/board/BIOS/chassis, with
/// /sys/class/dmi/id fallback), thermal zones, hwmon.
/// macOS:   sysctl, system_profiler SPHardwareDataType.
/// Windows: PowerShell Get-CimInstance (Win32_Processor, Win32_ComputerSystem,
/// Win32_BIOS, Win32_BaseBoard).
/// </summary>
public sealed class HardwareCollector : OsDispatchLocalCollector
{
    public override string Name => "hardware";
    private static readonly ILogger<HardwareCollector> Log = AgentLog.CreateLogger<HardwareCollector>();

    protected override void CollectCommon(string deviceId, List<Fact> facts)
    {
        facts.Add(Fact.Create(FactPaths.HwCpuLogicalCores, [deviceId], Environment.ProcessorCount));
        facts.Add(Fact.Create(FactPaths.HwOSArch, [deviceId], RuntimeInformation.OSArchitecture.ToString()));
    }

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        ParseCpuInfo(deviceId, facts);
        ParseMemInfo(deviceId, facts);

        // Prefer dmidecode -q (richer, single source) for system/board/BIOS/chassis identity,
        // falling back to /sys/class/dmi/id when dmidecode is unavailable (no root / not
        // installed) so behavior is unchanged on hosts without passwordless sudo.
        IReadOnlyList<DmiDecode.Section> dmi = await DmiDecode.RunAsync(ct);
        DmiDecode.Section? system = DmiDecode.Find(dmi, "System Information");
        DmiDecode.Section? board = DmiDecode.Find(dmi, "Base Board Information");
        DmiDecode.Section? bios = DmiDecode.Find(dmi, "BIOS Information");
        DmiDecode.Section? chassis = DmiDecode.Find(dmi, "Chassis Information");

        // Existing scalars: preserve the original always-emit behavior (may be empty), but
        // take the cleaned dmidecode value when present.
        static string Pick(DmiDecode.Section? s, string field, string sysfsFile) =>
            DmiDecode.Clean(DmiDecode.Get(s, field)) ?? ReadDmi(sysfsFile);

        facts.Add(Fact.Create(FactPaths.HwSystemVendor, [deviceId], Pick(system, "Manufacturer", "sys_vendor")));
        facts.Add(Fact.Create(FactPaths.HwSystemModel, [deviceId], Pick(system, "Product Name", "product_name")));
        facts.Add(Fact.Create(FactPaths.HwSystemSerial, [deviceId], Pick(system, "Serial Number", "product_serial")));
        facts.Add(Fact.Create(FactPaths.HwBoardVendor, [deviceId], Pick(board, "Manufacturer", "board_vendor")));
        facts.Add(Fact.Create(FactPaths.HwBoardModel, [deviceId], Pick(board, "Product Name", "board_name")));
        facts.Add(Fact.Create(FactPaths.HwBiosVendor, [deviceId], Pick(bios, "Vendor", "bios_vendor")));
        facts.Add(Fact.Create(FactPaths.HwBiosVersion, [deviceId], Pick(bios, "Version", "bios_version")));
        facts.Add(Fact.Create(FactPaths.HwBiosDate, [deviceId], Pick(bios, "Release Date", "bios_date")));

        // Chassis (new): dmidecode preferred; vendor/serial/asset-tag fall back to sysfs.
        // Chassis type has no useful sysfs string (chassis_type is a numeric SMBIOS code), so
        // it is emitted only from dmidecode's human-readable value. Emit only when non-empty.
        AddChassis(FactPaths.HwChassisVendor, DmiDecode.Get(chassis, "Manufacturer"), ReadDmi("chassis_vendor"));
        AddChassis(FactPaths.HwChassisType, DmiDecode.Get(chassis, "Type"), "");
        AddChassis(FactPaths.HwChassisSerial, DmiDecode.Get(chassis, "Serial Number"), ReadDmi("chassis_serial"));
        AddChassis(FactPaths.HwChassisAssetTag, DmiDecode.Get(chassis, "Asset Tag"), ReadDmi("chassis_asset_tag"));

        void AddChassis(string path, string? dmiValue, string sysfsValue)
        {
            string? value = DmiDecode.Clean(dmiValue) ?? DmiDecode.Clean(sysfsValue);
            if (value is not null)
            {
                facts.Add(Fact.Create(path, [deviceId], value));
            }
        }

        string? virt = await DetectLinuxVirtAsync(ct);
        if (virt is not null)
        {
            facts.Add(Fact.Create(FactPaths.HwVirtualization, [deviceId], virt));
        }

        CollectTemperatures(deviceId, facts);
    }

    private static void ParseCpuInfo(string deviceId, List<Fact> facts)
    {
        try
        {
            string model = "", vendor = "";
            double mhz = 0;
            int cores = 0;
            HashSet<string> seenPhysIds = new();

            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                string key = line[..colon].Trim();
                string val = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "model name" when model == "": model = val; break;
                    case "vendor_id" when vendor == "": vendor = val; break;
                    case "cpu MHz" when mhz == 0:
                        double.TryParse(
                            val,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out mhz
                        ); break;
                    case "physical id": seenPhysIds.Add(val); break;
                    case "cpu cores" when cores == 0: _ = int.TryParse(val, out cores); break;
                }
            }

            if (cores == 0)
            {
                cores = seenPhysIds.Count;
            }

            if (model != "")
            {
                facts.Add(Fact.Create(FactPaths.HwCpuModel, [deviceId], model));
            }

            if (vendor != "")
            {
                facts.Add(Fact.Create(FactPaths.HwCpuVendor, [deviceId], vendor));
            }

            if (mhz > 0)
            {
                facts.Add(Fact.Create(FactPaths.HwCpuMhz, [deviceId], mhz));
            }

            if (cores > 0)
            {
                facts.Add(Fact.Create(FactPaths.HwCpuCores, [deviceId], cores));
            }
        }
        catch (Exception ex)
        {
            HardwareCollectorLog.CpuInfoFailed(Log, ex);
        }
    }

    private static void ParseMemInfo(string deviceId, List<Fact> facts)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && ulong.TryParse(parts[1], out ulong kb))
                {
                    facts.Add(Fact.Create(FactPaths.HwTotalMemBytes, [deviceId], (long)(kb * 1024)));
                }

                break;
            }
        }
        catch (Exception ex)
        {
            HardwareCollectorLog.MemInfoFailed(Log, ex);
        }
    }

    private static string ReadDmi(string file)
    {
        try { return File.ReadAllText($"/sys/class/dmi/id/{file}").Trim(); }
        catch { return ""; }
    }

    private static async Task<string?> DetectLinuxVirtAsync(CancellationToken ct)
    {
        try
        {
            string out_ = await CollectorHelper.RunAsync("systemd-detect-virt", "--quiet", ct);
            string result = out_.Trim();
            return result is "" or "none" ? null : result;
        }
        catch (Exception ex)
        {
            HardwareCollectorLog.SystemdDetectVirtFailed(Log, ex);
            return null;
        }
    }

    private static void CollectTemperatures(string deviceId, List<Fact> facts)
    {
        try
        {
            foreach (string zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                if (!TryReadMilliCelsius($"{zone}/temp", out double c))
                {
                    continue;
                }

                string type = TryReadText($"{zone}/type") ?? Path.GetFileName(zone);
                facts.Add(Fact.Create(FactPaths.HwTemperatureCelsius, [deviceId, type], c));
            }
        }
        catch (Exception ex)
        {
            HardwareCollectorLog.ThermalZoneFailed(Log, ex);
        }

        try
        {
            foreach (string chip in Directory.EnumerateDirectories("/sys/class/hwmon"))
            {
                string chipName = TryReadText($"{chip}/name") ?? Path.GetFileName(chip);
                for (int n = 1; n <= 32; n++)
                {
                    if (!TryReadMilliCelsius($"{chip}/temp{n}_input", out double c))
                    {
                        break;
                    }

                    string label = TryReadText($"{chip}/temp{n}_label") ?? $"temp{n}";
                    facts.Add(Fact.Create(FactPaths.HwTemperatureCelsius, [deviceId, $"{chipName}/{label}"], c));
                }
            }
        }
        catch (Exception ex)
        {
            HardwareCollectorLog.HwmonFailed(Log, ex);
        }
    }

    private static bool TryReadMilliCelsius(string path, out double celsius)
    {
        celsius = 0;
        try
        {
            if (!long.TryParse(File.ReadAllText(path).Trim(), out long milli))
            {
                return false;
            }

            celsius = milli / 1000.0;
            return celsius is > 0 and <= 150;
        }
        catch { return false; }
    }

    private static string? TryReadText(string path)
    {
        try
        {
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
        string model = (await CollectorHelper.RunAsync("sysctl", "-n hw.model", ct)).Trim();
        string cpuModel = (await CollectorHelper.RunAsync("sysctl", "-n machdep.cpu.brand_string", ct)).Trim();
        string cpuVendor = (await CollectorHelper.RunAsync("sysctl", "-n machdep.cpu.vendor", ct)).Trim();

        if (int.TryParse((await CollectorHelper.RunAsync("sysctl", "-n hw.physicalcpu", ct)).Trim(), out int cores))
        {
            facts.Add(Fact.Create(FactPaths.HwCpuCores, [deviceId], cores));
        }

        if (double.TryParse(
                (await CollectorHelper.RunAsync("sysctl", "-n hw.cpufrequency", ct)).Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double hz
            )
         && hz > 0)
        {
            facts.Add(Fact.Create(FactPaths.HwCpuMhz, [deviceId], hz / 1_000_000.0));
        }

        if (ulong.TryParse((await CollectorHelper.RunAsync("sysctl", "-n hw.memsize", ct)).Trim(), out ulong memBytes))
        {
            facts.Add(Fact.Create(FactPaths.HwTotalMemBytes, [deviceId], (long)memBytes));
        }

        if (cpuModel != "")
        {
            facts.Add(Fact.Create(FactPaths.HwCpuModel, [deviceId], cpuModel));
        }

        if (cpuVendor != "")
        {
            facts.Add(Fact.Create(FactPaths.HwCpuVendor, [deviceId], cpuVendor));
        }

        if (model != "")
        {
            facts.Add(Fact.Create(FactPaths.HwSystemModel, [deviceId], model));
        }

        facts.Add(Fact.Create(FactPaths.HwSystemVendor, [deviceId], "Apple Inc."));

        // Serial number via system_profiler (text output)
        string profiler = await CollectorHelper.RunAsync("system_profiler", "SPHardwareDataType", ct);
        foreach (string line in profiler.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Serial Number (system):", StringComparison.OrdinalIgnoreCase))
            {
                string serial = trimmed["Serial Number (system):".Length..].Trim();
                if (serial != "")
                {
                    facts.Add(Fact.Create(FactPaths.HwSystemSerial, [deviceId], serial));
                }

                break;
            }
        }

        // Virtualization: kern.hv_vmm_present = "1" when running inside a VM
        string vmm = (await CollectorHelper.RunAsync("sysctl", "-n kern.hv_vmm_present", ct)).Trim();
        facts.Add(Fact.Create(FactPaths.HwVirtualization, [deviceId], vmm == "1" ? "vm" : "none"));
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    protected override async Task CollectWindowsAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // Single PowerShell round-trip collects system, BIOS, board, and CPU.
        const string script = """
            $o = [ordered]@{
                System = Get-CimInstance Win32_ComputerSystem  | Select-Object Manufacturer, Model, TotalPhysicalMemory
                BIOS   = Get-CimInstance Win32_BIOS            | Select-Object Manufacturer, SMBIOSBIOSVersion, SerialNumber
                Board  = Get-CimInstance Win32_BaseBoard       | Select-Object Manufacturer, Product
                CPU    = Get-CimInstance Win32_Processor | Select-Object -First 1 Name, Manufacturer, NumberOfCores, MaxClockSpeed
            }
            $o | ConvertTo-Json -Compress -Depth 4
            """;

        WindowsHwData? result = await CollectorHelper.RunPsJsonOneAsync<WindowsHwData>(script, ct);
        if (result is null)
        {
            return;
        }

        if (result.System is { } sys)
        {
            facts.AddIfPresent(FactPaths.HwSystemVendor, [deviceId], sys.Manufacturer);
            facts.AddIfPresent(FactPaths.HwSystemModel, [deviceId], sys.Model);
            if (sys.TotalPhysicalMemory > 0)
            {
                facts.Add(Fact.Create(FactPaths.HwTotalMemBytes, [deviceId], (long)sys.TotalPhysicalMemory));
            }
        }

        if (result.BIOS is { } bios)
        {
            facts.AddIfPresent(FactPaths.HwBiosVendor, [deviceId], bios.Manufacturer);
            facts.AddIfPresent(FactPaths.HwBiosVersion, [deviceId], bios.SMBIOSBIOSVersion);
            facts.AddIfPresent(FactPaths.HwSystemSerial, [deviceId], bios.SerialNumber);
        }

        if (result.Board is { } board)
        {
            facts.AddIfPresent(FactPaths.HwBoardVendor, [deviceId], board.Manufacturer);
            facts.AddIfPresent(FactPaths.HwBoardModel, [deviceId], board.Product);
        }

        if (result.CPU is { } cpu)
        {
            facts.AddIfPresent(FactPaths.HwCpuModel, [deviceId], cpu.Name?.Trim());
            facts.AddIfPresent(FactPaths.HwCpuVendor, [deviceId], cpu.Manufacturer);
            if (cpu.NumberOfCores > 0)
            {
                facts.Add(Fact.Create(FactPaths.HwCpuCores, [deviceId], cpu.NumberOfCores));
            }

            if (cpu.MaxClockSpeed > 0)
            {
                facts.Add(Fact.Create(FactPaths.HwCpuMhz, [deviceId], (double)cpu.MaxClockSpeed));
            }
        }

        // Virtualization from model name
        string modelStr = result.System?.Model?.ToLowerInvariant() ?? "";
        string virt = modelStr switch
        {
            var m when m.Contains("vmware") => "vmware",
            var m when m.Contains("virtualbox") => "virtualbox",
            var m when m.Contains("virtual machine")
             || m.Contains("hyperv") => "hyperv",
            var m when m.Contains("kvm")
             || m.Contains("qemu") => "kvm",
            var m when m.Contains("xen") => "xen",
            _ => "none",
        };
        facts.Add(Fact.Create(FactPaths.HwVirtualization, [deviceId], virt));
    }

    private sealed class WindowsHwData
    {
        public SystemData? System { get; set; }
        public BiosData? BIOS { get; set; }
        public BoardData? Board { get; set; }
        public CpuData? CPU { get; set; }

        public sealed class SystemData
        {
            public string? Manufacturer { get; set; }
            public string? Model { get; set; }
            public ulong TotalPhysicalMemory { get; set; }
        }

        public sealed class BiosData
        {
            public string? Manufacturer { get; set; }
            public string? SMBIOSBIOSVersion { get; set; }
            public string? SerialNumber { get; set; }
        }

        public sealed class BoardData
        {
            public string? Manufacturer { get; set; }
            public string? Product { get; set; }
        }

        public sealed class CpuData
        {
            public string? Name { get; set; }
            public string? Manufacturer { get; set; }
            public int NumberOfCores { get; set; }
            public int MaxClockSpeed { get; set; }
        }
    }
}

internal static partial class HardwareCollectorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not parse /proc/cpuinfo.")]
    internal static partial void CpuInfoFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not parse /proc/meminfo.")]
    internal static partial void MemInfoFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "systemd-detect-virt failed.")]
    internal static partial void SystemdDetectVirtFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read thermal zone temperatures.")]
    internal static partial void ThermalZoneFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read hwmon temperatures.")]
    internal static partial void HwmonFailed(ILogger logger, Exception ex);
}