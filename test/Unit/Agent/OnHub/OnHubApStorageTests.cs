using JMW.Discovery.Agent.Collection.Device.OnHub;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Covers parsing the AP's storage layout from <c>findmnt</c> tree output: real
/// device-backed filesystems are kept (pseudo mounts dropped), and the distinct
/// underlying parent block devices are surfaced as disks.
/// </summary>
public sealed class OnHubApStorageTests
{
    // Real report excerpt (Chrome OS on a Google Wifi): root on a dm device, the
    // stateful partition + several bind mounts on one eMMC partition, plus pseudo
    // filesystems that must be filtered out.
    private const string FindmntOutput =
        """
        TARGET                           SOURCE                        FSTYPE   OPTIONS
        /                                /dev/dm-0                     ext2     ro,relatime
        |-/dev                           devtmpfs                      devtmpfs rw,nosuid,noexec
        | |-/dev/shm                     shmfs                         tmpfs    rw,nosuid,nodev
        |-/proc                          proc                          proc     ro,nosuid
        |-/sys                           sysfs                         sysfs    rw,nosuid
        | `-/sys/fs/cgroup               none                          tmpfs    rw,nosuid,mode=755
        |-/tmp                           tmpfs                         tmpfs    rw,nosuid
        |-/mnt/stateful_partition        /dev/mmcblk0p1                ext4     rw,nosuid,noatime
        |-/usr/share/oem                 /dev/mmcblk0p8                ext4     ro,nosuid
        |-/home                          /dev/mmcblk0p1[/home]         ext4     rw,nosuid,noatime
        | `-/home/chronos                /dev/mmcblk0p1[/home/chronos] ext4     rw,nosuid,noatime
        |-/var                           /dev/mmcblk0p1[/var]          ext4     rw,nosuid,noatime
        `-/media                         media                         tmpfs    rw,nosuid
        """;

    [Fact]
    public void Parse_KeepsOnlyDeviceBackedFilesystems()
    {
        (IReadOnlyList<OnHubFilesystem> fs, _) = OnHubApStorage.Parse(FindmntOutput);

        // Pseudo mounts (/dev, /proc, /sys, /tmp, /media, cgroup, shm) are dropped.
        Assert.Equal(
            new[]
            {
                "/",
                "/mnt/stateful_partition",
                "/usr/share/oem",
                "/home",
                "/home/chronos",
                "/var",
            },
            fs.Select(f => f.Mount).ToArray()
        );
        Assert.All(fs.Where(f => f.Mount != "/"), f => Assert.Equal("ext4", f.FsType));
        Assert.Equal("ext2", fs.Single(f => f.Mount == "/").FsType);
    }

    [Fact]
    public void Parse_SurfacesDistinctParentDisks()
    {
        (_, IReadOnlyList<string> disks) = OnHubApStorage.Parse(FindmntOutput);

        // /dev/dm-0 → dm-0 (kept whole); the four mmcblk0p1 + one mmcblk0p8 → mmcblk0.
        Assert.Equal(
            new[]
            {
                "dm-0",
                "mmcblk0",
            },
            disks.ToArray()
        );
    }

    [Theory]
    [InlineData("mmcblk0p1", "mmcblk0")]
    [InlineData("mmcblk0p8", "mmcblk0")]
    [InlineData("mmcblk0", "mmcblk0")] // whole device, no partition
    [InlineData("nvme0n1p2", "nvme0n1")]
    [InlineData("nvme0n1", "nvme0n1")]
    [InlineData("sda1", "sda")]
    [InlineData("sda", "sda")]
    [InlineData("vdb3", "vdb")]
    [InlineData("dm-0", "dm-0")] // device-mapper: trailing digit is not a partition
    [InlineData("loop3", "loop3")]
    public void ParentDevice_ReducesPartitionToWholeDisk(string device, string expected) =>
        Assert.Equal(expected, OnHubApStorage.ParentDevice(device));

    [Theory]
    [InlineData("")]
    [InlineData("TARGET  SOURCE  FSTYPE  OPTIONS")] // header only, no '/'
    [InlineData("\n\n")]
    public void Parse_EmptyOrHeaderOnly_ReturnsNothing(string input)
    {
        (IReadOnlyList<OnHubFilesystem> fs, IReadOnlyList<string> disks) = OnHubApStorage.Parse(input);
        Assert.Empty(fs);
        Assert.Empty(disks);
    }
}