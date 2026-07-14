using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers SNMP-enabled devices by sending an SNMPv2c GetRequest for
/// sysName (OID 1.3.6.1.2.1.1.5.0) to the subnet broadcast address on UDP
/// port 161, using the community string "public". Parses the BER-encoded
/// GetResponse-PDU by hand to pull out the sysName value. Catches devices
/// that answer SNMP broadcasts but aren't otherwise visible via ARP.
/// Source tag: "snmp-broadcast".
/// </summary>
public sealed class SnmpBroadcastScanner : INetworkScanner
{
    public string Name => "snmp-broadcast";
    public bool IsSupported => true;

    // SNMPv2c GET request for sysName (1.3.6.1.2.1.1.5.0) with community "public"
    private static readonly byte[] SysNameGetRequest =
    {
        0x30,
        0x29, // SEQUENCE (41 bytes)
        0x02,
        0x01,
        0x01, // version = 1 (SNMPv2c)
        0x04,
        0x06,
        0x70,
        0x75,
        0x62,
        0x6C,
        0x69,
        0x63, // community "public"
        0xA0,
        0x1C, // GetRequest-PDU (28 bytes)
        0x02,
        0x04,
        0x00,
        0x00,
        0x00,
        0x01, // request-id = 1
        0x02,
        0x01,
        0x00, // error-status = 0
        0x02,
        0x01,
        0x00, // error-index = 0
        0x30,
        0x0E, // VarBindList (14 bytes)
        0x30,
        0x0C, // VarBind (12 bytes)
        0x06,
        0x08,
        0x2B,
        0x06,
        0x01,
        0x02,
        0x01,
        0x01,
        0x05,
        0x00, // OID 1.3.6.1.2.1.1.5.0
        0x05,
        0x00, // NULL
    };

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        Dictionary<string, DiscoveredDevice> seen = [];

        try
        {
            using UdpClient udp = new(new IPEndPoint(target.LocalAddress, 0));
            udp.EnableBroadcast = true;

            IPAddress broadcast = GetBroadcast(target.SubnetAddress, target.PrefixLength);
            IPEndPoint snmpEndpoint = new(broadcast, 161);
            await udp.SendAsync(SysNameGetRequest, SysNameGetRequest.Length, snmpEndpoint);

            await foreach (UdpReceiveResult result in SocketProbe.CollectResponsesAsync(udp, 3000, ct))
            {
                string sourceIp = result.RemoteEndPoint.Address.ToString();

                if (seen.ContainsKey(sourceIp))
                {
                    continue;
                }

                string? sysName = ParseSysName(result.Buffer);
                if (sysName is null)
                {
                    continue;
                }

                seen[sourceIp] = new DiscoveredDevice
                {
                    IpAddress = sourceIp,
                    Hostname = sysName,
                    Source = Name,
                    Attributes = new Dictionary<string, string>
                    {
                        ["snmp.sysname"] = sysName,
                    },
                };
            }
        }
        catch
        {
            return [];
        }

        return [.. seen.Values];
    }

    private static string? ParseSysName(byte[] data)
    {
        // Walk the BER-encoded response to find the OCTET STRING value of sysName.
        // Structure: SEQUENCE > version > community > GetResponse-PDU > ... > VarBind > OID > OCTET STRING
        // We scan forward past the outer wrapper to find the first OCTET STRING (tag 0x04)
        // that follows the OID tag (0x06), which is the sysName value.
        int pos = 0;

        if (!TrySkipTag(data, ref pos, 0x30)) // outer SEQUENCE
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x02)) // version
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x04)) // community string
        {
            return null;
        }

        if (!TrySkipTag(data, ref pos, 0xA2)) // GetResponse-PDU
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x02)) // request-id
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x02)) // error-status
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x02)) // error-index
        {
            return null;
        }

        if (!TrySkipTag(data, ref pos, 0x30)) // VarBindList
        {
            return null;
        }

        if (!TrySkipTag(data, ref pos, 0x30)) // VarBind
        {
            return null;
        }

        if (!TrySkipTlv(data, ref pos, 0x06)) // OID
        {
            return null;
        }

        // Next TLV should be the OCTET STRING value
        if (pos >= data.Length || data[pos] != 0x04)
        {
            return null;
        }

        pos++;

        if (pos >= data.Length)
        {
            return null;
        }

        int length = ReadLength(data, ref pos);
        if (length < 0 || length > 256 || pos + length > data.Length)
        {
            return null;
        }

        return Encoding.UTF8.GetString(data, pos, length);
    }

    private static bool TrySkipTag(byte[] data, ref int pos, byte expectedTag)
    {
        if (pos >= data.Length || data[pos] != expectedTag)
        {
            return false;
        }

        pos++;
        int len = ReadLength(data, ref pos);
        if (len < 0)
        {
            return false;
        }

        return true;
    }

    private static bool TrySkipTlv(byte[] data, ref int pos, byte expectedTag)
    {
        if (pos >= data.Length || data[pos] != expectedTag)
        {
            return false;
        }

        pos++;
        int len = ReadLength(data, ref pos);
        if (len < 0 || pos + len > data.Length)
        {
            return false;
        }

        pos += len;
        return true;
    }

    // Reads a BER length field and advances pos past it. Returns -1 on error.
    private static int ReadLength(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
        {
            return -1;
        }

        byte first = data[pos++];

        if ((first & 0x80) == 0)
        {
            return first;
        }

        int numBytes = first & 0x7F;
        if (numBytes == 0 || pos + numBytes > data.Length)
        {
            return -1;
        }

        int length = 0;
        for (int i = 0; i < numBytes; i++)
        {
            length = (length << 8) | data[pos++];
        }

        return length;
    }

    private static IPAddress GetBroadcast(IPAddress subnet, int prefix)
    {
        byte[] bytes = subnet.GetAddressBytes();
        int hostBits = 32 - prefix;
        for (int i = 3; i >= 0 && hostBits > 0; i--, hostBits -= 8)
        {
            int bits = Math.Min(hostBits, 8);
            bytes[i] |= (byte)((1 << bits) - 1);
        }

        return new IPAddress(bytes);
    }
}