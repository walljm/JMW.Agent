using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects listening TCP/UDP port facts.
/// Uses IPGlobalProperties (cross-platform, no subprocess) for the endpoint list.
/// Process names are added on each platform via:
/// Linux:   ss -tulnp
/// macOS:   lsof -nP -i tcp -sTCP:LISTEN
/// Windows: PowerShell Get-NetTCPConnection correlated with Get-Process
/// </summary>
public sealed class PortCollector : ILocalCollector
{
    public string Name => "port";
    public bool IsSupported => true;

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        // Cross-platform: get endpoints via .NET API (no process names).
        Dictionary<int, (string? Name, int Pid)> procMap = await BuildProcessMapAsync(ct);

        IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();

        foreach (IPEndPoint ep in props.GetActiveTcpListeners())
        {
            string addr = ep.Address.ToString();
            string proto = ep.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "tcp6" : "tcp";
            string portKey = $"{proto}:{addr}:{ep.Port}";
            string[] keys = [deviceId, portKey];

            facts.Add(Fact.Create(FactPaths.PortProtocol, keys, proto));
            facts.Add(Fact.Create(FactPaths.PortAddress, keys, addr));
            facts.Add(Fact.Create(FactPaths.PortNumber, keys, ep.Port));

            if (procMap.TryGetValue(ep.Port, out (string? Name, int Pid) proc))
            {
                facts.AddIfPresent(FactPaths.PortProcessName, keys, proc.Name);

                if (proc.Pid > 0)
                {
                    facts.Add(Fact.Create(FactPaths.PortPid, keys, proc.Pid));
                }
            }
        }

        foreach (IPEndPoint ep in props.GetActiveUdpListeners())
        {
            string addr = ep.Address.ToString();
            string proto = ep.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "udp6" : "udp";
            string portKey = $"{proto}:{addr}:{ep.Port}";
            string[] keys = [deviceId, portKey];

            facts.Add(Fact.Create(FactPaths.PortProtocol, keys, proto));
            facts.Add(Fact.Create(FactPaths.PortAddress, keys, addr));
            facts.Add(Fact.Create(FactPaths.PortNumber, keys, ep.Port));
        }

        return facts;
    }

    // ── Process name resolution per platform ──────────────────────────────────

    private static Task<Dictionary<int, (string? Name, int Pid)>> BuildProcessMapAsync(
        CancellationToken ct
    )
    {
        if (OperatingSystem.IsLinux())
        {
            return BuildLinuxProcessMapAsync(ct);
        }

        if (OperatingSystem.IsMacOS())
        {
            return BuildMacOsProcessMapAsync(ct);
        }

        if (OperatingSystem.IsWindows())
        {
            return BuildWindowsProcessMapAsync(ct);
        }

        return Task.FromResult(new Dictionary<int, (string?, int)>());
    }

    // Linux: ss -tulnp — same parser as before, builds port → (name, pid)
    private static async Task<Dictionary<int, (string? Name, int Pid)>> BuildLinuxProcessMapAsync(
        CancellationToken ct
    )
    {
        Dictionary<int, (string? Name, int Pid)> map = new();
        string output = await CollectorHelper.RunAsync("ss", "-tulnp", ct);

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || (parts[0] != "tcp" && parts[0] != "udp"))
            {
                continue;
            }

            string local = parts[4];
            int lastColon = local.LastIndexOf(':');
            if (lastColon < 0 || !int.TryParse(local[(lastColon + 1)..], out int port))
            {
                continue;
            }

            string? userField = parts.FirstOrDefault(p => p.StartsWith("users:", StringComparison.OrdinalIgnoreCase));
            if (userField is null)
            {
                continue;
            }

            if (TryParseProcess(userField, out string? name, out int pid))
            {
                map.TryAdd(port, (name, pid));
            }
        }

        return map;
    }

    // macOS: lsof -nP -i tcp -sTCP:LISTEN
    private static async Task<Dictionary<int, (string? Name, int Pid)>> BuildMacOsProcessMapAsync(
        CancellationToken ct
    )
    {
        Dictionary<int, (string? Name, int Pid)> map = new();
        string output = await CollectorHelper.RunAsync("lsof", "-nP -i tcp -sTCP:LISTEN", ct);

        // COMMAND  PID USER  FD TYPE DEVICE SIZE/OFF NODE NAME
        // nginx    123 root  6u IPv4  ...   TCP *:80 (LISTEN)
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9)
            {
                continue;
            }

            if (!int.TryParse(parts[1], out int pid))
            {
                continue;
            }

            string name = parts[0]; // COMMAND
            string addr = parts[^2]; // e.g. "*:80" or "127.0.0.1:443"
            int colonIdx = addr.LastIndexOf(':');
            if (colonIdx < 0 || !int.TryParse(addr[(colonIdx + 1)..], out int port))
            {
                continue;
            }

            map.TryAdd(port, (name, pid));
        }

        return map;
    }

    // Windows: PowerShell Get-NetTCPConnection correlated with Get-Process
    [SupportedOSPlatform("windows")]
    private static async Task<Dictionary<int, (string? Name, int Pid)>> BuildWindowsProcessMapAsync(
        CancellationToken ct
    )
    {
        Dictionary<int, (string? Name, int Pid)> map = new();

        // Get-NetTCPConnection gives LocalPort → OwningProcess (PID).
        // Get-Process gives PID → ProcessName.
        const string script = """
            $procs = @{}; Get-Process | ForEach-Object { $procs[$_.Id] = $_.ProcessName }
            Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Select-Object LocalPort,
              @{n='ProcessName';e={$procs[$_.OwningProcess]}},
              @{n='PID';e={$_.OwningProcess}} |
            ConvertTo-Json -Compress
            """;

        List<TcpRow> rows = await CollectorHelper.RunPsJsonAsync<TcpRow>(script, ct);
        foreach (TcpRow r in rows)
        {
            if (r.LocalPort > 0)
            {
                map.TryAdd(r.LocalPort, (r.ProcessName, r.PID));
            }
        }

        return map;
    }

    private sealed class TcpRow
    {
        public int LocalPort { get; set; }
        public string? ProcessName { get; set; }
        public int PID { get; set; }
    }

    private static bool TryParseProcess(string field, out string? name, out int pid)
    {
        name = null;
        pid = 0;
        int start = field.IndexOf("((\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += 3;
        int end = field.IndexOf('"', start);
        if (end < 0)
        {
            return false;
        }

        name = field[start..end];
        int pidIdx = field.IndexOf("pid=", end, StringComparison.Ordinal);
        if (pidIdx < 0)
        {
            return true;
        }

        int pidStart = pidIdx + 4;
        int pidEnd = field.IndexOfAny([',', ')'], pidStart);
        if (pidEnd >= 0)
        {
            _ = int.TryParse(field[pidStart..pidEnd], out pid);
        }

        return true;
    }
}