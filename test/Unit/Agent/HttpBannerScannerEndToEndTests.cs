using System.Net;
using System.Net.Sockets;
using System.Text;

using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// End-to-end test of the full <see cref="HttpBannerScanner"/> probe against a loopback HTTP server:
/// port probe → banner (title/status) → favicon fetch+hash → identity resolve → adaptive UPnP
/// follow-up → merge → emitted attributes. Uses the injectable port set to target only the test
/// listener (deterministic, no dependency on which host ports happen to be free).
/// </summary>
public sealed class HttpBannerScannerEndToEndTests
{
    private const string RootHtml =
        "<html><head><title>Login</title><link rel=\"icon\" href=\"/favicon.ico\"></head>"
        + "<body><a href=\"/rootDesc.xml\">device description</a></body></html>";

    private const string UpnpXml =
        "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device>"
        + "<friendlyName>Test Router</friendlyName><manufacturer>Netgear</manufacturer>"
        + "<modelName>R7000</modelName><serialNumber>SN12345</serialNumber></device></root>";

    [Fact]
    public async Task Scan_ResolvesIdentityViaBannerFaviconAndUpnpFollowUp()
    {
        int port = GetFreeLoopbackPort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        Task server = ServeAsync(listener, cts.Token);

        HttpBannerScanner scanner = new([port]);
        NetworkScanTarget target = new()
        {
            SubnetAddress = IPAddress.Parse("127.0.0.0"),
            PrefixLength = 8,
            LocalAddress = IPAddress.Parse("127.0.0.53"),
            Neighbors = [new Neighbor(IPAddress.Loopback, null)],
        };

        IReadOnlyList<DiscoveredDevice> devices = await scanner.ScanAsync(target, CancellationToken.None);

        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await server;
        }
        catch
        {
            // listener torn down
        }

        DiscoveredDevice device = Assert.Single(devices);
        Dictionary<string, string> a = device.Attributes;

        // Banner path
        Assert.Equal("Login", a["http.title"]);
        Assert.Equal("200", a["http.status"]);

        // Favicon path
        Assert.True(a.ContainsKey("http.favicon.md5"), "favicon md5 missing");
        Assert.True(a.ContainsKey("http.favicon.mmh3"), "favicon mmh3 missing");

        // Adaptive UPnP follow-up overrides the banner guess with self-declared identity
        Assert.Equal("Netgear", a["http.identity.vendor"]);
        Assert.Equal("R7000", a["http.identity.model"]);
        Assert.Equal("SN12345", a["http.identity.serial"]);
        Assert.Equal("Test Router", a["http.identity.name"]);
        Assert.Equal("0.95", a["http.identity.confidence"]);
        Assert.Contains("upnp", a["http.identity.source"]);
    }

    private static int GetFreeLoopbackPort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task ServeAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(ct);
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            (string contentType, byte[] body) = path switch
            {
                "/favicon.ico" => ("image/x-icon", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                "/rootDesc.xml" => ("text/xml", Encoding.UTF8.GetBytes(UpnpXml)),
                _ => ("text/html", Encoding.UTF8.GetBytes(RootHtml)),
            };

            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct);
            ctx.Response.Close();
        }
    }
}