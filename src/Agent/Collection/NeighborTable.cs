using System.Net;
using System.Runtime.Versioning;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Resolves the host's IPv4 neighbor (ARP/NDP) table once per cycle, OS-dispatched, so every
/// network scanner shares a single enumeration instead of each spawning its own
/// <c>ip neigh</c> / <c>arp -a</c> / <c>Get-NetNeighbor</c> subprocess (review D1). Reachable-neighbor
/// semantics are applied uniformly: Linux requires an <c>lladdr</c> (skips INCOMPLETE/FAILED), macOS
/// skips <c>incomplete</c>, Windows excludes <c>Unreachable</c>. The table spans all interfaces; the
/// collector filters it per subnet and excludes the host's own address before handing it to scanners.
/// </summary>
public static class NeighborTable
{
    public static async Task<IReadOnlyList<Neighbor>> ResolveAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            return await ResolveLinuxAsync(ct);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await ResolveMacOsAsync(ct);
        }

        if (OperatingSystem.IsWindows())
        {
            return await ResolveWindowsAsync(ct);
        }

        return [];
    }

    private static async Task<IReadOnlyList<Neighbor>> ResolveLinuxAsync(CancellationToken ct)
    {
        List<Neighbor> neighbors = new();
        string output = await CollectorHelper.RunAsync("ip", "neigh show", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5)
            {
                continue;
            }

            string? mac = null;
            for (int i = 1; i < tokens.Length - 1; i++)
            {
                if (tokens[i] == "lladdr")
                {
                    mac = tokens[i + 1];
                    break;
                }
            }

            // Reachable-neighbor semantics: an entry without an lladdr is INCOMPLETE/FAILED — skip it.
            if (mac is null)
            {
                continue;
            }

            if (IPAddress.TryParse(tokens[0], out IPAddress? parsed))
            {
                neighbors.Add(new Neighbor(parsed, mac));
            }
        }

        return neighbors;
    }

    private static async Task<IReadOnlyList<Neighbor>> ResolveMacOsAsync(CancellationToken ct)
    {
        List<Neighbor> neighbors = new();
        string output = await CollectorHelper.RunAsync("arp", "-a", ct);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();

            if (trimmed.Contains("incomplete", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int ipStart = trimmed.IndexOf('(');
            int ipEnd = trimmed.IndexOf(')');
            if (ipStart < 0 || ipEnd < 0 || ipEnd <= ipStart + 1)
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed[(ipStart + 1)..ipEnd], out IPAddress? parsed))
            {
                continue;
            }

            string? mac = null;
            int atIdx = trimmed.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
            if (atIdx >= 0)
            {
                string[] parts = trimmed[(atIdx + 4)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    mac = parts[0];
                }
            }

            neighbors.Add(new Neighbor(parsed, mac));
        }

        return neighbors;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IReadOnlyList<Neighbor>> ResolveWindowsAsync(CancellationToken ct)
    {
        const string script =
            "Get-NetNeighbor -AddressFamily IPv4 | Where-Object {$_.State -ne 'Unreachable'} | Select-Object IPAddress,LinkLayerAddress | ConvertTo-Json -Compress";

        List<WinNeighbor> entries = await CollectorHelper.RunPsJsonAsync<WinNeighbor>(script, ct);
        List<Neighbor> neighbors = new();
        foreach (WinNeighbor e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.IpAddress) || !IPAddress.TryParse(e.IpAddress, out IPAddress? parsed))
            {
                continue;
            }

            neighbors.Add(
                new Neighbor(parsed, string.IsNullOrWhiteSpace(e.LinkLayerAddress) ? null : e.LinkLayerAddress)
            );
        }

        return neighbors;
    }

    private sealed class WinNeighbor
    {
        public string? IpAddress { get; set; }
        public string? LinkLayerAddress { get; set; }
    }
}