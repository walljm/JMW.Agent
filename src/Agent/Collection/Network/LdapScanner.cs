using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers LDAP directory servers by probing port 389 on ARP-known neighbors
/// with an anonymous RootDSE search request (base scope, present("objectClass")
/// filter). Parses the raw BER/LDAP response for dnsHostName, defaultNamingContext,
/// and serverName. Source tag: "ldap". Useful for identifying domain controllers
/// and other directory servers even when they aren't otherwise advertised on the
/// network.
/// </summary>
public sealed class LdapScanner : UnicastScannerBase
{
    public override string Name => "ldap";

    protected override int MaxConcurrency => 20;

    // SearchRequest: baseObject="" scope=base filter=present("objectClass") attrs=all
    private static readonly byte[] RootDseRequest = new byte[]
    {
        0x30,
        0x2C,
        0x02,
        0x01,
        0x01,
        0x63,
        0x27,
        0x04,
        0x00,
        0x0A,
        0x01,
        0x00,
        0x0A,
        0x01,
        0x00,
        0x02,
        0x01,
        0x00,
        0x02,
        0x01,
        0x1E,
        0x01,
        0x01,
        0x00,
        0x87,
        0x0B,
        0x6F,
        0x62,
        0x6A,
        0x65,
        0x63,
        0x74,
        0x43,
        0x6C,
        0x61,
        0x73,
        0x73,
        0x30,
        0x00,
    };

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcp = await SocketProbe.TryConnectAsync(ip, 389, 2000, ct);
            if (tcp is null)
            {
                return null;
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            using NetworkStream stream = tcp.GetStream();
            await stream.WriteAsync(RootDseRequest, linked.Token);

            byte[] buffer = new byte[4096];
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), linked.Token);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;

                if (IsLdapMessageComplete(buffer, totalRead))
                {
                    break;
                }
            }

            if (totalRead < 2)
            {
                return null;
            }

            return ParseLdapResponse(ip, buffer, totalRead);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLdapMessageComplete(byte[] buffer, int length)
    {
        if (length < 2)
        {
            return false;
        }

        if (buffer[0] != 0x30)
        {
            return false;
        }

        int headerLen = BerLengthSize(buffer, 1);
        if (length < 1 + headerLen)
        {
            return false;
        }

        int contentLen = ReadBerLength(buffer, 1);
        return length >= 1 + headerLen + contentLen;
    }

    private static DiscoveredDevice? ParseLdapResponse(string ip, byte[] data, int length)
    {
        string? dnsHostName = null;
        string? defaultNamingContext = null;
        string? serverName = null;

        int offset = 0;
        while (offset < length)
        {
            if (!ReadBerTlv(data, length, ref offset, out byte tag, out byte[] value))
            {
                break;
            }

            // SearchResultEntry is APPLICATION tag 4 = 0x64
            if (tag == 0x64)
            {
                ParseSearchResultEntry(value, ref dnsHostName, ref defaultNamingContext, ref serverName);
            }
        }

        if (dnsHostName == null && defaultNamingContext == null)
        {
            return null;
        }

        Dictionary<string, string> attributes = [];
        if (defaultNamingContext != null)
        {
            attributes["ldap.naming_context"] = defaultNamingContext;
        }

        if (serverName != null)
        {
            attributes["ldap.server_name"] = serverName;
        }

        return new DiscoveredDevice
        {
            IpAddress = ip,
            Hostname = dnsHostName,
            Source = "ldap",
            Attributes = attributes,
        };
    }

    private static void ParseSearchResultEntry(
        byte[] data,
        ref string? dnsHostName,
        ref string? defaultNamingContext,
        ref string? serverName
    )
    {
        int offset = 0;

        // objectName: OCTET STRING (skip it)
        if (!ReadBerTlv(data, data.Length, ref offset, out byte _, out byte[] _))
        {
            return;
        }

        // PartialAttributeList: SEQUENCE OF
        if (!ReadBerTlv(data, data.Length, ref offset, out byte listTag, out byte[] listValue))
        {
            return;
        }

        if (listTag != 0x30)
        {
            return;
        }

        int listOffset = 0;
        while (listOffset < listValue.Length)
        {
            if (!ReadBerTlv(listValue, listValue.Length, ref listOffset, out byte attrTag, out byte[] attrValue))
            {
                break;
            }

            if (attrTag != 0x30)
            {
                continue;
            }

            int attrOffset = 0;

            if (!ReadBerTlv(attrValue, attrValue.Length, ref attrOffset, out byte _, out byte[] nameBytes))
            {
                continue;
            }

            string attrName = Encoding.UTF8.GetString(nameBytes);

            if (!ReadBerTlv(attrValue, attrValue.Length, ref attrOffset, out byte _, out byte[] valSetBytes))
            {
                continue;
            }

            int valOffset = 0;
            if (!ReadBerTlv(valSetBytes, valSetBytes.Length, ref valOffset, out byte _, out byte[] firstVal))
            {
                continue;
            }

            string attrValue2 = Encoding.UTF8.GetString(firstVal);

            if (attrName.Equals("dnsHostName", StringComparison.OrdinalIgnoreCase))
            {
                dnsHostName = attrValue2;
            }
            else if (attrName.Equals("defaultNamingContext", StringComparison.OrdinalIgnoreCase))
            {
                defaultNamingContext = attrValue2;
            }
            else if (attrName.Equals("serverName", StringComparison.OrdinalIgnoreCase))
            {
                serverName = attrValue2;
            }
        }
    }

    private static bool ReadBerTlv(byte[] data, int length, ref int offset, out byte tag, out byte[] value)
    {
        tag = 0;
        value = [];

        if (offset >= length)
        {
            return false;
        }

        tag = data[offset++];

        if (offset >= length)
        {
            return false;
        }

        int contentLen = ReadBerLength(data, offset);
        int headerLen = BerLengthSize(data, offset);
        offset += headerLen;

        if (offset + contentLen > length)
        {
            return false;
        }

        value = new byte[contentLen];
        Array.Copy(data, offset, value, 0, contentLen);
        offset += contentLen;

        return true;
    }

    private static int ReadBerLength(byte[] data, int offset)
    {
        if (offset >= data.Length)
        {
            return 0;
        }

        byte first = data[offset];
        if ((first & 0x80) == 0)
        {
            return first;
        }

        int numBytes = first & 0x7F;
        int len = 0;
        for (int i = 1; i <= numBytes && offset + i < data.Length; i++)
        {
            len = (len << 8) | data[offset + i];
        }

        return len;
    }

    private static int BerLengthSize(byte[] data, int offset)
    {
        if (offset >= data.Length)
        {
            return 1;
        }

        byte first = data[offset];
        if ((first & 0x80) == 0)
        {
            return 1;
        }

        return 1 + (first & 0x7F);
    }
}