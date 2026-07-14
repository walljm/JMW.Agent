using JMW.Discovery.Server.Agents;

namespace JMW.Discovery.Tests;

public sealed class ReleaseManagerTests : IDisposable
{
    private readonly string _root;

    public ReleaseManagerTests()
    {
        _root = Directory.CreateTempSubdirectory("jmw-releases-test-").FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteRelease(string version, string filename, string contents, string? signature = null)
    {
        string dir = Path.Combine(_root, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, filename), contents);
        if (signature is not null)
        {
            File.WriteAllText(Path.Combine(dir, filename + ".sig"), signature);
        }
    }

    [Fact]
    public void Disabled_WhenNoDirectoryIsConfigured()
    {
        ReleaseManager manager = new(null);

        Assert.False(manager.Enabled);
        Assert.Null(manager.Latest("linux", "x64"));
        Assert.Null(manager.Lookup("v1.0.0", "jmw-agent-linux-x64"));
    }

    [Fact]
    public void Scan_MissingDirectory_TreatedAsNoReleasesPublished()
    {
        ReleaseManager manager = new(Path.Combine(_root, "does-not-exist"));

        manager.Scan();

        Assert.True(manager.Enabled);
        Assert.Null(manager.Latest("linux", "x64"));
    }

    [Fact]
    public void Scan_IndexesAValidRelease_WithComputedHashAndSignature()
    {
        WriteRelease("v1.2.3", "jmw-agent-linux-x64", "hello world", signature: "c2ln");
        ReleaseManager manager = new(_root);

        manager.Scan();

        ReleaseEntry? entry = manager.Latest("linux", "x64");
        Assert.NotNull(entry);
        Assert.Equal("v1.2.3", entry.Version);
        Assert.Equal("linux", entry.Os);
        Assert.Equal("x64", entry.Arch);
        Assert.Equal("jmw-agent-linux-x64", entry.Filename);
        Assert.Equal(11, entry.Size); // "hello world".Length
        Assert.Equal("c2ln", entry.Signature);
        // sha256("hello world")
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", entry.Sha256);
    }

    [Fact]
    public void Scan_EntryWithNoSigSidecar_HasEmptySignature()
    {
        WriteRelease("v1.0.0", "jmw-agent-linux-x64", "abc");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.Equal("", manager.Latest("linux", "x64")!.Signature);
    }

    [Theory]
    [InlineData("v1.2.3-dirty")] // build metadata / prerelease — never eligible
    [InlineData("1.2.3")] // missing leading 'v'
    [InlineData("dev")]
    [InlineData("v1.2")] // not a full MAJOR.MINOR.PATCH
    public void Scan_IgnoresDirectoriesThatAreNotACleanSemverTag(string versionDirName)
    {
        WriteRelease(versionDirName, "jmw-agent-linux-x64", "abc");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.Null(manager.Latest("linux", "x64"));
    }

    [Theory]
    [InlineData("jmw-agent-linux")] // missing arch
    [InlineData("some-other-binary")] // doesn't match the convention at all
    [InlineData("jmw-agent-LINUX-x64")] // uppercase not allowed by the pattern
    public void Scan_IgnoresFilesThatDoNotMatchTheBinaryNamingConvention(string filename)
    {
        WriteRelease("v1.0.0", filename, "abc");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.Empty(manager.All());
    }

    [Fact]
    public void Scan_PicksTheHighestSemverPerPlatform()
    {
        WriteRelease("v1.0.0", "jmw-agent-linux-x64", "old");
        WriteRelease("v2.0.0", "jmw-agent-linux-x64", "new");
        WriteRelease("v1.5.0", "jmw-agent-linux-x64", "mid");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.Equal("v2.0.0", manager.Latest("linux", "x64")!.Version);
    }

    [Fact]
    public void Scan_TracksEachPlatformIndependently()
    {
        WriteRelease("v1.0.0", "jmw-agent-linux-x64", "a");
        WriteRelease("v2.0.0", "jmw-agent-windows-x64.exe", "b");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.Equal("v1.0.0", manager.Latest("linux", "x64")!.Version);
        Assert.Equal("v2.0.0", manager.Latest("windows", "x64")!.Version);
        Assert.Null(manager.Latest("linux", "arm64"));
    }

    [Fact]
    public void Lookup_ReturnsTheExactVersionFilenamePair_EvenWhenNotTheLatest()
    {
        WriteRelease("v1.0.0", "jmw-agent-linux-x64", "old");
        WriteRelease("v2.0.0", "jmw-agent-linux-x64", "new");
        ReleaseManager manager = new(_root);

        manager.Scan();

        Assert.NotNull(manager.Lookup("v1.0.0", "jmw-agent-linux-x64"));
        Assert.Equal("v1.0.0", manager.Lookup("v1.0.0", "jmw-agent-linux-x64")!.Version);
        Assert.Null(manager.Lookup("v9.9.9", "jmw-agent-linux-x64"));
        Assert.Null(manager.Lookup("v1.0.0", "no-such-file"));
    }

    [Theory]
    [InlineData("v1.0.0", "v2.0.0", true)]
    [InlineData("v2.0.0", "v1.0.0", false)]
    [InlineData("v1.0.0", "v1.0.0", false)]
    [InlineData("v1.2.3", "v1.2.10", true)] // numeric, not lexicographic, comparison
    [InlineData("v1.0.0-rc1", "v1.0.0", true)] // a clean release beats a prerelease at the same MAJOR.MINOR.PATCH
    public void SemverGreater_OrdersVersionsNumerically(string a, string b, bool expected)
    {
        Assert.Equal(expected, ReleaseManager.SemverGreater(a, b));
    }
}