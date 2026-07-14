using System.Net.Sockets;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers MQTT brokers by probing ports 1883 and 8883 on ARP-known neighbors.
/// Sends a minimal CONNECT packet and inspects the CONNACK return code, treating
/// both success and auth-required responses as confirmation that an MQTT broker
/// is present. Useful for surfacing IoT/home-automation message brokers.
/// Source tag: "mqtt".
/// </summary>
public sealed class MqttScanner : UnicastScannerBase
{
    public override string Name => "mqtt";

    private static readonly int[] ProbePorts = [1883, 8883];

    private static readonly byte[] ConnectPacket;

    static MqttScanner()
    {
        byte[] clientId = "jmw-discovery"u8.ToArray();
        int payloadLen = 2 + clientId.Length;
        int varHeaderLen = 10;
        int remainingLength = varHeaderLen + payloadLen;
        List<byte> pkt = new()
        {
            0x10,
            (byte)remainingLength,
            0x00,
            0x04,
            0x4D,
            0x51,
            0x54,
            0x54,
            0x04,
            0x02,
            0x00,
            0x3C,
            (byte)(clientId.Length >> 8),
            (byte)(clientId.Length & 0xFF),
        };
        pkt.AddRange(clientId);
        ConnectPacket = pkt.ToArray();
    }

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

            await stream.WriteAsync(ConnectPacket, cts.Token);

            byte[] buffer = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(bytesRead, 4 - bytesRead), cts.Token);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            if (bytesRead < 4)
            {
                return null;
            }

            if (buffer[0] != 0x20)
            {
                return null;
            }

            byte returnCode = buffer[3];
            if (returnCode != 0x00 && returnCode != 0x04 && returnCode != 0x05)
            {
                return null;
            }

            bool authRequired = returnCode == 0x04 || returnCode == 0x05;

            Dictionary<string, string> attributes = new()
            {
                ["mqtt.port"] = port.ToString(),
                ["mqtt.auth_required"] = authRequired ? "true" : "false",
                ["mqtt.return_code"] = $"0x{returnCode:X2}",
            };

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Source = "mqtt",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }
}