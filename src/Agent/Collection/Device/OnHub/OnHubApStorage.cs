using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>A device-backed mount point distilled from <c>findmnt</c>.</summary>
public sealed record OnHubFilesystem(string Mount, string? FsType);

/// <summary>
/// Parses the AP's storage layout from the diagnostic report's <c>/bin/findmnt</c>
/// output. findmnt prints a tree:
/// <code>
/// TARGET                           SOURCE                        FSTYPE   OPTIONS
/// /                                /dev/dm-0                     ext2     ro,relatime
/// |-/mnt/stateful_partition        /dev/mmcblk0p1                ext4     rw,...
/// |-/home                          /dev/mmcblk0p1[/home]         ext4     rw,...
/// </code>
/// Only device-backed rows (SOURCE under <c>/dev/</c>) are real filesystems; pseudo
/// mounts (proc, sysfs, tmpfs, cgroup, …) are dropped. There is no size data — findmnt
/// default output has no SIZE column and the report carries no <c>df</c>. Disks are the
/// distinct underlying block devices the mounts sit on.
/// </summary>
public static class OnHubApStorage
{
    private const string Command = "/bin/findmnt";

    public static (IReadOnlyList<OnHubFilesystem> Filesystems, IReadOnlyList<string> Disks) Extract(
        DiagnosticReport report
    )
    {
        foreach (CommandOutput cmd in report.CommandOutputs)
        {
            if (string.Equals(cmd.Command, Command, StringComparison.Ordinal))
            {
                return Parse(cmd.Output);
            }
        }

        return ([], []);
    }

    public static (IReadOnlyList<OnHubFilesystem> Filesystems, IReadOnlyList<string> Disks) Parse(string output)
    {
        List<OnHubFilesystem> filesystems = [];
        HashSet<string> seenMounts = new(StringComparer.Ordinal);
        List<string> disks = [];
        HashSet<string> seenDisks = new(StringComparer.Ordinal);

        foreach (string rawLine in output.Split('\n'))
        {
            // The TARGET column is prefixed with tree-drawing characters ("|-", "`-",
            // "| ", spaces); the real path begins at the first '/'. SOURCE/FSTYPE never
            // contain a leading '/', so the first '/' reliably marks the target.
            int slash = rawLine.IndexOf('/');
            if (slash < 0)
            {
                continue; // header row or blank
            }

            string[] cols = rawLine[slash..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 3)
            {
                continue;
            }

            string mount = cols[0];
            string source = cols[1];
            string fsType = cols[2];

            // Only real device-backed filesystems; pseudo sources (proc, tmpfs, …) skip.
            if (!source.StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }

            if (seenMounts.Add(mount))
            {
                filesystems.Add(new OnHubFilesystem(mount, fsType.Length > 0 ? fsType : null));
            }

            // SOURCE may carry a bind-mount subpath, e.g. "/dev/mmcblk0p1[/home]".
            string device = source["/dev/".Length..];
            int bracket = device.IndexOf('[');
            if (bracket >= 0)
            {
                device = device[..bracket];
            }

            string disk = ParentDevice(device);
            if (disk.Length > 0 && seenDisks.Add(disk))
            {
                disks.Add(disk);
            }
        }

        return (filesystems, disks);
    }

    /// <summary>
    /// Reduces a partition device name to its parent disk: <c>mmcblk0p1 → mmcblk0</c>,
    /// <c>nvme0n1p2 → nvme0n1</c>, <c>sda1 → sda</c>. Device-mapper nodes (<c>dm-0</c>)
    /// and already-whole devices are returned unchanged.
    /// </summary>
    public static string ParentDevice(string device)
    {
        // eMMC/NVMe partitions use a "p<N>" suffix (the base itself ends in a digit).
        if (device.StartsWith("mmcblk", StringComparison.Ordinal)
         || device.StartsWith("nvme", StringComparison.Ordinal))
        {
            int p = device.LastIndexOf('p');
            if (p > 0 && p < device.Length - 1 && AllDigits(device, p + 1))
            {
                return device[..p];
            }

            return device;
        }

        // Device-mapper / loop / ram nodes end in digits but are whole devices.
        if (device.StartsWith("dm-", StringComparison.Ordinal)
         || device.StartsWith("loop", StringComparison.Ordinal)
         || device.StartsWith("ram", StringComparison.Ordinal))
        {
            return device;
        }

        // Conventional disks (sd*, vd*, hd*, xvd*) partition by trailing digits.
        int end = device.Length;
        while (end > 0 && char.IsDigit(device[end - 1]))
        {
            end--;
        }

        return end > 0 ? device[..end] : device;
    }

    private static bool AllDigits(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i]))
            {
                return false;
            }
        }

        return start < s.Length;
    }
}