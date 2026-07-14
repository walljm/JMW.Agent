using System.Net;
using System.Net.Sockets;

using JMW.Discovery.Agent.Collection.Device;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers BACnet/IP devices via Who-Is broadcast on UDP port 47808.
/// Devices respond with I-Am unicast replies containing their device instance
/// and vendor ID. Source tag: "bacnet".
/// </summary>
public sealed class BacnetScanner : INetworkScanner
{
    private const int BacnetPort = 47808;

    public string Name => "bacnet";
    public bool IsSupported => true;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        List<DiscoveredDevice> devices = [];

        try
        {
            IPAddress broadcast = ComputeBroadcast(target.SubnetAddress, target.PrefixLength);
            byte[] whoIs = BacnetClient.BuildWhoIsRequest();

            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));
            udp.EnableBroadcast = true;

            await udp.SendAsync(whoIs, whoIs.Length, new IPEndPoint(broadcast, BacnetPort));

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 3000, ct))
            {
                string ip = result.RemoteEndPoint.Address.ToString();

                if (ip == target.LocalAddress.ToString())
                {
                    continue;
                }

                // Reject responders outside the target subnet to prevent amplification abuse.
                if (!target.Contains(result.RemoteEndPoint.Address))
                {
                    continue;
                }

                DiscoveredDevice? device = TryParseIAm(ip, result.Buffer);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }
        }
        catch
        {
            // network unavailable or unsupported on this interface
        }

        return devices;
    }

    private static DiscoveredDevice? TryParseIAm(string ip, byte[] data)
    {
        try
        {
            // Validate BVLL: must be BACnet/IP (0x81) unicast reply (0x0A)
            if (data.Length < 6)
            {
                return null;
            }

            if (data[0] != 0x81)
            {
                return null;
            }

            if (data[1] != 0x0A)
            {
                return null; // Original-Unicast-NPDU
            }

            ReadOnlySpan<byte> apdu = BacnetClient.StripBvllAndNpdu(data);
            (uint instance, ushort vendorId)? parsed = BacnetClient.ParseIAm(apdu);
            if (parsed is null)
            {
                return null;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Source = "bacnet",
                Attributes =
                {
                    ["bacnet.instance"] = parsed.Value.instance.ToString(),
                    ["bacnet.vendor_id"] = parsed.Value.vendorId.ToString(),
                },
            };
        }
        catch
        {
            return null;
        }
    }

    private static IPAddress ComputeBroadcast(IPAddress subnet, int prefixLength)
    {
        byte[] bytes = subnet.GetAddressBytes();
        int hostBits = 32 - prefixLength;
        for (int i = 3; i >= 0 && hostBits > 0; i--)
        {
            int bits = Math.Min(hostBits, 8);
            bytes[i] |= (byte)(0xFF >> (8 - bits));
            hostBits -= bits;
        }

        return new IPAddress(bytes);
    }
}