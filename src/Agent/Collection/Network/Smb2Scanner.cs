using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers SMB2 file/print servers by probing port 445 on ARP-known
/// neighbors. Sends a raw SMB2 NEGOTIATE request and, on a valid SMB2 reply,
/// extracts the negotiated dialect and recovers the server's NetBIOS/hostname
/// from the TargetName field embedded in the NTLM challenge. Useful for
/// identifying Windows hosts and NAS/Samba devices. Source tag: "smb2".
/// </summary>
public sealed class Smb2Scanner : UnicastScannerBase
{
    public override string Name => "smb2";

    private static readonly byte[] NegotiateRequest = new byte[]
    {
        // NetBIOS Session
        0x00,
        0x00,
        0x00,
        0x54,
        // SMB2 Header
        0xFE,
        0x53,
        0x4D,
        0x42,
        0x40,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x01,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        // NEGOTIATE body
        0x24,
        0x00,
        0x03,
        0x00,
        0x01,
        0x00,
        0x00,
        0x00,
        0x7F,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x02,
        0x02,
        0x10,
        0x02,
        0x00,
        0x03,
    };

    private static readonly byte[] NtlmsspSignature = [0x4E, 0x54, 0x4C, 0x4D, 0x53, 0x53, 0x50, 0x00];

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcp = await SocketProbe.TryConnectAsync(ip, 445, 2000, ct);
            if (tcp is null)
            {
                return null;
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            using NetworkStream stream = tcp.GetStream();
            await stream.WriteAsync(NegotiateRequest, linked.Token);

            byte[] header = new byte[4];
            int read = await ReadExactAsync(stream, header, linked.Token);
            if (read < 4)
            {
                return null;
            }

            int payloadLength = (header[1] << 16) | (header[2] << 8) | header[3];
            if (payloadLength <= 0 || payloadLength > 65536)
            {
                return null;
            }

            byte[] payload = new byte[payloadLength];
            read = await ReadExactAsync(stream, payload, linked.Token);
            if (read < payloadLength)
            {
                return null;
            }

            if (payload.Length < 4
             || payload[0] != 0xFE
             || payload[1] != 0x53
             || payload[2] != 0x4D
             || payload[3] != 0x42)
            {
                return null;
            }

            string? hostname = ExtractNtlmTargetName(payload);
            string? dialect = ExtractDialect(payload);

            Dictionary<string, string> attributes = [];
            if (dialect != null)
            {
                attributes["smb2.dialect"] = dialect;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = hostname,
                Source = "smb2",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static string? ExtractNtlmTargetName(byte[] payload)
    {
        int sigPos = IndexOf(payload, NtlmsspSignature);
        if (sigPos < 0)
        {
            return null;
        }

        // NTLM challenge: sig(8) + msgtype(4) = 12 bytes before TargetName fields
        int fieldsOffset = sigPos + 12;

        // TargetName fields: length(2) + maxlen(2) + offset(4)
        if (fieldsOffset + 8 > payload.Length)
        {
            return null;
        }

        int nameLength = (payload[fieldsOffset + 1] << 8) | payload[fieldsOffset];
        int nameOffset = (payload[fieldsOffset + 7] << 24)
          | (payload[fieldsOffset + 6] << 16)
          | (payload[fieldsOffset + 5] << 8)
          | payload[fieldsOffset + 4];

        if (nameOffset < 0 || nameOffset > payload.Length - sigPos)
        {
            return null;
        }

        int absoluteOffset = sigPos + nameOffset;
        if (nameLength <= 0 || absoluteOffset + nameLength > payload.Length)
        {
            return null;
        }

        return Encoding.Unicode.GetString(payload, absoluteOffset, nameLength);
    }

    private static string? ExtractDialect(byte[] payload)
    {
        // SMB2 NEGOTIATE response: 64-byte header, then StructureSize(2) + SecurityMode(2) +
        // DialectRevision(2) at offset 68
        if (payload.Length < 70)
        {
            return null;
        }

        int dialectValue = (payload[69] << 8) | payload[68];
        return dialectValue switch
        {
            0x0202 => "2.0.2",
            0x0210 => "2.1",
            0x0300 => "3.0",
            0x0302 => "3.0.2",
            0x0311 => "3.1.1",
            _ => $"0x{dialectValue:X4}",
        };
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}