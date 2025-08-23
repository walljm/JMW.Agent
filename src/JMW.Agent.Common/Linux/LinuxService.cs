using JMW.Agent.Common.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace JMW.Agent.Common;

internal static class LinuxService
{
    private const string ArpTablePath = "/proc/net/arp";

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists(ArpTablePath);

    public static IEnumerable<JmwNetNeighbor> ReadArpTable()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException();
        }

        using var arpFile = new FileStream(ArpTablePath, FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(arpFile);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.ToLower().Contains("ip address"))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6)
            {
                continue;
            }

            yield return new JmwNetNeighbor
            {
                IPAddress = IPAddress.TryParse(fields[0], out var ip) ? new JmwIpAddress(ip) : null,
                LinkLayerAddress = PhysicalAddress.TryParse(fields[3], out var physicalAddress) ? physicalAddress : null,
                InterfaceAlias = fields[5],
                State = flags.ContainsKey(fields[2]) ? flags[fields[2]] : null,
            };
        }
    }

    private static readonly Dictionary<string, State> flags = new()
    {
        {"0x0", State.Incomplete},
        {"0x2", State.Reachable},
        {"0x4", State.Permanent},
    };
}
