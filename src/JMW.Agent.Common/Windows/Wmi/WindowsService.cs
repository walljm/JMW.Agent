using System.Management;
using System.Runtime.InteropServices;

namespace JMW.Agent.Common.Models;

internal class WindowsService
{
    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static MSFT_NetNeighbor[] GetNetNeighbors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Array.Empty<MSFT_NetNeighbor>();
        }

        var scope = new ManagementScope($@"\\localhost\root\StandardCimv2");

        return QueryCim<MSFT_NetNeighbor>(scope, "MSFT_NetNeighbor").ToArray();
    }

    private static IEnumerable<T> QueryCim<T>(ManagementScope scope, string cls)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var s = new ManagementObjectSearcher(scope, new WqlObjectQuery($"SELECT * FROM {cls}"));

        foreach (var o in s.Get().Cast<ManagementObject>())
        {
            var t = o.ToType<T>();
            if (t is null)
            {
                continue;
            }
            yield return t;
        }
    }
}
