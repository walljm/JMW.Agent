using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

public sealed class GpuCollector : OsDispatchLocalCollector
{
    public override string Name => "gpu";

    // ── Linux ─────────────────────────────────────────────────────────────────

    protected override async Task CollectLinuxAsync(
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        try
        {
            string output = await CollectorHelper.RunAsync(
                "nvidia-smi",
                "--query-gpu=index,name,driver_version,memory.total --format=csv,noheader,nounits",
                ct
            );

            List<(int index, string name, string driver, long vramMb)> nvidiaGpus = new();
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split(',');
                if (parts.Length < 4)
                {
                    continue;
                }

                if (!int.TryParse(parts[0].Trim(), out int idx))
                {
                    continue;
                }

                string name = parts[1].Trim();
                string driver = parts[2].Trim();
                _ = long.TryParse(parts[3].Trim(), out long vramMb);
                nvidiaGpus.Add((idx, name, driver, vramMb));
            }

            if (nvidiaGpus.Count > 0)
            {
                foreach ((int index, string name, string driver, long vramMb) in nvidiaGpus)
                {
                    string[] gpuKeys = [deviceId, index.ToString()];
                    facts.Add(Fact.Create(FactPaths.GpuName, gpuKeys, name));
                    facts.Add(Fact.Create(FactPaths.GpuVendor, gpuKeys, "NVIDIA"));
                    facts.Add(Fact.Create(FactPaths.GpuVramMB, gpuKeys, vramMb));
                    facts.Add(Fact.Create(FactPaths.GpuDriverVersion, gpuKeys, driver));
                }

                return;
            }
        }
        catch { }

        try
        {
            string lspci = await CollectorHelper.RunAsync("lspci", "-mmv", ct);
            ParseLspci(deviceId, lspci, facts);
        }
        catch { }
    }

    private static void ParseLspci(string deviceId, string output, List<Fact> facts)
    {
        string[] gpuClasses = ["VGA compatible controller", "Display controller", "3D controller"];

        string? vendor = null;
        string? device = null;
        bool isGpu = false;
        int index = 0;

        foreach (string line in output.Split('\n'))
        {
            if (line.Length == 0)
            {
                if (isGpu && device is not null)
                {
                    string[] gpuKeys = [deviceId, index.ToString()];
                    facts.Add(Fact.Create(FactPaths.GpuName, gpuKeys, device));
                    facts.Add(Fact.Create(FactPaths.GpuVendor, gpuKeys, vendor ?? ""));
                    facts.Add(Fact.Create(FactPaths.GpuVramMB, gpuKeys, 0L));
                    facts.Add(Fact.Create(FactPaths.GpuDriverVersion, gpuKeys, ""));
                    index++;
                }

                vendor = null;
                device = null;
                isGpu = false;
                continue;
            }

            int tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            string fieldName = line[..tab].TrimEnd(':');
            string value = line[(tab + 1)..].Trim();

            switch (fieldName)
            {
                case "Class":
                    isGpu = Array.Exists(gpuClasses, c => value.Contains(c, StringComparison.OrdinalIgnoreCase));
                    break;
                case "Vendor":
                    vendor = value;
                    break;
                case "Device":
                    device = value;
                    break;
            }
        }

        if (isGpu && device is not null)
        {
            string[] gpuKeys = [deviceId, index.ToString()];
            facts.Add(Fact.Create(FactPaths.GpuName, gpuKeys, device));
            facts.Add(Fact.Create(FactPaths.GpuVendor, gpuKeys, vendor ?? ""));
            facts.Add(Fact.Create(FactPaths.GpuVramMB, gpuKeys, 0L));
            facts.Add(Fact.Create(FactPaths.GpuDriverVersion, gpuKeys, ""));
        }
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
            string json = await CollectorHelper.RunAsync(
                "system_profiler",
                "SPDisplaysDataType -json",
                ct
            );

            MacDisplaysRoot? root = JsonSerializer.Deserialize<MacDisplaysRoot>(json);
            if (root?.SPDisplaysDataType is null)
            {
                return;
            }

            for (int i = 0; i < root.SPDisplaysDataType.Count; i++)
            {
                MacGpu gpu = root.SPDisplaysDataType[i];
                string[] gpuKeys = [deviceId, i.ToString()];
                facts.Add(Fact.Create(FactPaths.GpuName, gpuKeys, gpu.Model ?? ""));
                facts.Add(Fact.Create(FactPaths.GpuVendor, gpuKeys, ExtractMacVendor(gpu.Vendor)));
                facts.Add(Fact.Create(FactPaths.GpuVramMB, gpuKeys, ParseMacVram(gpu.Vram)));
                facts.Add(Fact.Create(FactPaths.GpuDriverVersion, gpuKeys, ""));
            }
        }
        catch { }
    }

    private static string ExtractMacVendor(string? vendor)
    {
        if (vendor is null)
        {
            return "";
        }

        string[] known = ["NVIDIA", "AMD", "Intel", "Apple"];
        foreach (string v in known)
        {
            if (vendor.Contains(v, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        // "Radeon" is AMD branding
        if (vendor.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        return vendor;
    }

    private static long ParseMacVram(string? vram)
    {
        if (vram is null)
        {
            return 0;
        }

        string[] parts = vram.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
        {
            return 0;
        }

        string unit = parts[1].ToUpperInvariant();
        return unit switch
        {
            "GB" => (long)(amount * 1024),
            "MB" => (long)amount,
            _ => 0,
        };
    }

    private sealed class MacDisplaysRoot
    {
        [JsonPropertyName("SPDisplaysDataType")]
        public List<MacGpu>? SPDisplaysDataType { get; set; }
    }

    private sealed class MacGpu
    {
        [JsonPropertyName("sppci_model")]
        public string? Model { get; set; }

        [JsonPropertyName("spdisplays_vram")]
        public string? Vram { get; set; }

        [JsonPropertyName("spdisplays_vendor")]
        public string? Vendor { get; set; }
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
            const string script =
                "Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM,DriverVersion,VideoProcessor | ConvertTo-Json -Compress";

            List<WinGpu> gpus = await CollectorHelper.RunPsJsonAsync<WinGpu>(script, ct);
            for (int i = 0; i < gpus.Count; i++)
            {
                WinGpu gpu = gpus[i];
                string[] gpuKeys = [deviceId, i.ToString()];
                facts.Add(Fact.Create(FactPaths.GpuName, gpuKeys, gpu.Name ?? ""));
                facts.Add(Fact.Create(FactPaths.GpuVendor, gpuKeys, ExtractWindowsVendor(gpu.Name)));
                facts.Add(Fact.Create(FactPaths.GpuVramMB, gpuKeys, gpu.AdapterRAM / (1024L * 1024L)));
                facts.Add(Fact.Create(FactPaths.GpuDriverVersion, gpuKeys, gpu.DriverVersion ?? ""));
            }
        }
        catch { }
    }

    private static string ExtractWindowsVendor(string? name)
    {
        if (name is null)
        {
            return "";
        }

        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
         || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        if (name.Contains("Apple", StringComparison.OrdinalIgnoreCase))
        {
            return "Apple";
        }

        return "";
    }

    private sealed class WinGpu
    {
        public string? Name { get; set; }
        public long AdapterRAM { get; set; }
        public string? DriverVersion { get; set; }
        public string? VideoProcessor { get; set; }
    }
}