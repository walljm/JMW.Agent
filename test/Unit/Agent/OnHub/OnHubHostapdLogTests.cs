using System.IO.Compression;
using System.Text;

using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

public sealed class OnHubHostapdLogTests
{
    private const string LogPath = "/var/log/messages";

    // Real line shapes verified against a live capture (docs/scratch/deep-dive-2.md §1).
    private const string Log =
        """
        2026-07-13T09:43:00.000000Z INFO hostapd[2513]: wlan-5000mhz: STA 3886f79d14d*      IEEE 802.11: Station 3886f79d14d*      has been active 12s ago
        2026-07-13T20:11:41.300311Z INFO hostapd[2513]: wlan-5000mhz: STA 3886f79d14d*      IEEE 802.11: Station 3886f79d14d*      has been active 0s ago
        2026-07-13T20:15:44.272924Z INFO hostapd[2513]: wlan-2400mhz: STA 187f889c4e6*      IEEE 802.11: Station 187f889c4e6*      has been active 6s ago
        2026-07-13T20:30:02.515930Z INFO hostapd[2513]: guest-5000mhz: STA 7286489dd01*      IAPP: Received IAPP ADD-notify (seq# 0) from 192.168.1.217:3517 (STA not found)
        2026-07-13T20:34:40.410486Z INFO hostapd[2513]: guest-5000mhz: STA 7286489dd01*      IAPP: Received IAPP ADD-notify (seq# 0) from 192.168.1.215:3517 (STA not found)
        some unrelated non-UTF8-ish line with no structure at all
        """;

    private static DiagnosticReport ReportWithFile(string path, byte[] content)
    {
        DiagnosticReport report = new();
        report.Files.Add(
            new JMW.Discovery.Agent.Collection.Device.OnHub.Proto.File
            {
                Path = path,
                Content = Google.Protobuf.ByteString.CopyFrom(content),
            }
        );
        return report;
    }

    [Fact]
    public void Extract_PlainTextFile_ParsesLatestActiveAndRoamingPerMac()
    {
        DiagnosticReport report = ReportWithFile(LogPath, Encoding.UTF8.GetBytes(Log));

        IReadOnlyDictionary<string, OnHubHostapdActivity> byMac = OnHubHostapdLog.ExtractByObscuredMac(report);

        // Two "active" lines for the same MAC — keeps the LATEST timestamp, not the first.
        OnHubHostapdActivity? a = byMac.GetValueOrDefault("3886f79d14d*");
        Assert.NotNull(a);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T20:11:41.300311Z"), a.LastActiveAt);
        Assert.Equal("wlan-5000mhz", a.LastActiveInterface);

        OnHubHostapdActivity? b = byMac.GetValueOrDefault("187f889c4e6*");
        Assert.NotNull(b);
        Assert.Equal("wlan-2400mhz", b.LastActiveInterface);

        // Two roaming events for the same MAC — keeps the latest AP IP (.215, not .217).
        OnHubHostapdActivity? c = byMac.GetValueOrDefault("7286489dd01*");
        Assert.NotNull(c);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T20:34:40.410486Z"), c.LastRoamingAt);
        Assert.Equal("192.168.1.215", c.LastRoamingApIp);
    }

    [Fact]
    public void Extract_GzipCompressedFile_DecompressesAndParses()
    {
        byte[] plain = Encoding.UTF8.GetBytes(Log);
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        DiagnosticReport report = ReportWithFile(LogPath, compressed.ToArray());

        IReadOnlyDictionary<string, OnHubHostapdActivity> byMac = OnHubHostapdLog.ExtractByObscuredMac(report);

        Assert.True(byMac.ContainsKey("3886f79d14d*"));
    }

    [Fact]
    public void Extract_WrongFilePath_ReturnsEmpty()
    {
        DiagnosticReport report = ReportWithFile("/var/log/net.log", Encoding.UTF8.GetBytes(Log));

        Assert.Empty(OnHubHostapdLog.ExtractByObscuredMac(report));
    }

    [Fact]
    public void Extract_NoFiles_ReturnsEmpty()
    {
        Assert.Empty(OnHubHostapdLog.ExtractByObscuredMac(new DiagnosticReport()));
    }
}