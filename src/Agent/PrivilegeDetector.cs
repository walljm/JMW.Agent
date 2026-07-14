using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace JMW.Discovery.Agent;

/// <summary>
/// Detects whether the agent has raw-socket privileges at startup and
/// reports a passive discovery mode: "full" when raw sockets are available,
/// "degraded" when they are not.
/// </summary>
public static class PrivilegeDetector
{
    private static readonly Lazy<string> _mode = new(Detect);

    /// <summary>"full" or "degraded".</summary>
    public static string PassiveDiscoveryMode => _mode.Value;

    private static string Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return DetectWindows();
        }

        // Linux and macOS: probe with a raw ICMP socket.
        try
        {
            using Socket s = new(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            return "full";
        }
        catch (SocketException)
        {
            return "degraded";
        }
        catch (Exception)
        {
            return "degraded";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DetectWindows()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? "full"
                : "degraded";
        }
        catch (Exception)
        {
            return "degraded";
        }
    }
}