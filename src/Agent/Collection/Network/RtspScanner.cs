using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers RTSP-capable devices by probing ports 554 and 8554 on ARP-known
/// neighbors. Sends an RTSP OPTIONS request and confirms a match on a
/// "RTSP/1.0 200" reply, capturing the Server, Public (supported methods),
/// and Content-Type headers. Useful for surfacing IP cameras and streaming
/// encoders. Source tag: "rtsp".
/// </summary>
public sealed class RtspScanner : UnicastScannerBase
{
    public override string Name => "rtsp";

    private static readonly int[] ProbePorts = [554, 8554];

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            foreach (int port in ProbePorts)
            {
                DiscoveredDevice? device = await ProbePortAsync(ip, port, ct);
                if (device is not null)
                {
                    return device;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DiscoveredDevice?> ProbePortAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcp = await SocketProbe.TryConnectAsync(ip, port, 2000, ct);
            if (tcp is null)
            {
                return null;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            using NetworkStream stream = tcp.GetStream();
            stream.WriteTimeout = 2000;
            stream.ReadTimeout = 2000;

            string request =
                $"OPTIONS rtsp://{ip}:{port}/ RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: JMW-Discovery/1.0\r\n\r\n";
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, cts.Token);

            byte[] buffer = new byte[2048];
            int bytesRead = await stream.ReadAsync(buffer, cts.Token);
            if (bytesRead == 0)
            {
                return null;
            }

            string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
            return ParseResponse(ip, port, response);
        }
        catch
        {
            return null;
        }
    }

    private static DiscoveredDevice? ParseResponse(string ip, int port, string response)
    {
        string[] lines = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        if (!lines[0].StartsWith("RTSP/1.0 200", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Dictionary<string, string> headers = HttpHeaderLines.Parse(response);
        Dictionary<string, string> attributes = new()
        {
            ["rtsp.port"] = port.ToString(),
        };

        if (headers.TryGetValue("Server", out string? server))
        {
            attributes["rtsp.server"] = server;
        }

        if (headers.TryGetValue("Public", out string? methods))
        {
            attributes["rtsp.methods"] = methods;
        }

        if (headers.TryGetValue("Content-Type", out string? contentType))
        {
            attributes["rtsp.content_type"] = contentType;
        }

        return new DiscoveredDevice
        {
            IpAddress = ip,
            Source = "rtsp",
            Attributes = attributes,
        };
    }
}