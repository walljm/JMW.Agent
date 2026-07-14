using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts by sending a NetBIOS Name Service (NBNS) NBSTAT query to UDP
/// port 137 on ARP-known neighbors. Parses the returned NetBIOS name table to
/// recover the machine's NetBIOS name plus every name/flag/header field the
/// protocol carries, useful for identifying legacy Windows/SMB hosts that don't
/// respond to modern name-resolution protocols.
/// Source tag: "nbns".
/// </summary>
public sealed class NbnsScanner : UnicastScannerBase
{
    public override string Name => "nbns";

    protected override int MaxConcurrency => 50;

    private static readonly byte[] NbstatRequest = BuildNbstatRequest();

    private static byte[] BuildNbstatRequest()
    {
        List<byte> packet = [];

        // Transaction ID
        packet.AddRange([0xAB, 0xCD]);
        // Flags: query, NBSTAT
        packet.AddRange([0x00, 0x10]);
        // Questions
        packet.AddRange([0x00, 0x01]);
        // Answer RRs
        packet.AddRange([0x00, 0x00]);
        // Authority RRs
        packet.AddRange([0x00, 0x00]);
        // Additional RRs
        packet.AddRange([0x00, 0x00]);

        // Length prefix for the encoded name
        packet.Add(0x20);

        // Wildcard "*" (0x2A) encoded as two nibble chars, padded with 0x20 × 15, suffix 0x00
        byte[] encoded = EncodeName("*", 0x00);
        packet.AddRange(encoded);

        // Name terminator
        packet.Add(0x00);

        // Type NBSTAT (0x0021)
        packet.AddRange([0x00, 0x21]);
        // Class IN
        packet.AddRange([0x00, 0x01]);

        return [.. packet];
    }

    private static byte[] EncodeName(string name, byte suffix)
    {
        byte[] padded = new byte[16];
        byte[] nameBytes = Encoding.ASCII.GetBytes(name.ToUpper(CultureInfo.InvariantCulture));
        Array.Copy(nameBytes, padded, Math.Min(nameBytes.Length, 15));
        for (int i = nameBytes.Length; i < 15; i++)
        {
            padded[i] = 0x20;
        }

        padded[15] = suffix;

        byte[] result = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            result[i * 2] = (byte)(((padded[i] >> 4) & 0x0F) + 0x41);
            result[(i * 2) + 1] = (byte)((padded[i] & 0x0F) + 0x41);
        }

        return result;
    }

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            using UdpClient udp = new();

            IPEndPoint remote = new(IPAddress.Parse(ip), 137);
            await udp.SendAsync(NbstatRequest, NbstatRequest.Length, remote);

            try
            {
                using CancellationTokenSource receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                UdpReceiveResult result = await udp.ReceiveAsync(receiveTimeout.Token);
                return ParseResponse(ip, result.Buffer);
            }
            catch (SocketException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    // RFC 1002 §4.2.1.1 header OPCODE (bits 1-4 of the 16-bit flags word).
    private enum OpCode
    {
        Query = 0,
        Registration = 5,
        Release = 6,
        Wack = 7,
        Refresh = 8,
    }

    // RFC 1002 §4.2.1.1 header RCODE (bits 12-15 of the flags word).
    private enum ResultCode
    {
        Success = 0x0,
        FmtErr = 0x1,
        SrvErr = 0x2,
        NamErr = 0x3,
        ImpErr = 0x4,
        RfsErr = 0x5,
        ActErr = 0x6,
        CftErr = 0x7,
    }

    // RFC 1002 §4.2.18 NAME_FLAGS ONT (Owner Node Type, bits 1-2 of the flags word).
    private enum OwnerNodeType
    {
        Broadcast = 0b00,
        PointToPoint = 0b01,
        Mixed = 0b10,
        Reserved = 0b11,
    }

    public static DiscoveredDevice? ParseResponse(string ip, byte[] data)
    {
        const int HeaderLength = 12;
        if (data.Length < HeaderLength)
        {
            return null;
        }

        // RFC 1002 §4.2.1.1 header flags word (bytes 2-3, big-endian):
        //   bit0 R | bits1-4 OPCODE | bit5 AA | bit6 TC | bit7 RD | bit8 RA | bits9-10 (0) |
        //   bit11 B | bits12-15 RCODE
        // "bit N" here is RFC bit-numbering (0 = most significant, transmitted first), so
        // bit N of a big-endian ushort `w` is (w >> (15 - N)) & 1.
        int flagsWord = (data[2] << 8) | data[3];
        OpCode opCode = (OpCode)((flagsWord >> 11) & 0xF);
        bool isAuthoritative = (flagsWord & 0x0400) != 0; // bit5
        bool isTruncated = (flagsWord & 0x0200) != 0; // bit6
        bool isRecursionDesired = (flagsWord & 0x0100) != 0; // bit7
        bool isRecursionAvailable = (flagsWord & 0x0080) != 0; // bit8
        bool isBroadcast = (flagsWord & 0x0010) != 0; // bit11
        ResultCode resultCode = (ResultCode)(flagsWord & 0xF); // bits12-15

        Dictionary<string, string> attributes = new()
        {
            ["nbns.op_code"] = opCode.ToString(),
            ["nbns.result_code"] = resultCode.ToString(),
            ["nbns.authoritative"] = isAuthoritative.ToString(),
            ["nbns.truncated"] = isTruncated.ToString(),
            ["nbns.broadcast"] = isBroadcast.ToString(),
            ["nbns.recursion_desired"] = isRecursionDesired.ToString(),
            ["nbns.recursion_available"] = isRecursionAvailable.ToString(),
        };

        // RFC 1002 §4.2.18: an NBSTAT response is a standard DNS-style message. Some
        // responders echo the question section (as this scanner's own request does),
        // others don't, and the answer's NAME may or may not use compression — none of
        // that is fixed, so it must be walked generically rather than assumed at a
        // hardcoded byte offset. A previous fixed-offset implementation silently
        // misaligned against responders that omit the question section, reading the
        // name table starting mid-record and producing garbled, overlapping names.
        int qdCount = (data[4] << 8) | data[5];
        int anCount = (data[6] << 8) | data[7];

        string? machineName = null;
        if (anCount > 0 && TryParseNameTable(data, qdCount, out List<string> names, out List<string> nameDetails, out machineName))
        {
            attributes["nbns.names"] = string.Join(",", names);
            attributes["nbns.name_details"] = string.Join(",", nameDetails);
        }

        // A structurally valid response (even with no names, e.g. a non-SUCCESS RCODE)
        // still confirms this host speaks NBNS — worth keeping, not discarding.
        return new DiscoveredDevice
        {
            IpAddress = ip,
            Hostname = machineName,
            Source = "nbns",
            Attributes = attributes,
        };
    }

    private static bool TryParseNameTable(
        byte[] data,
        int qdCount,
        out List<string> names,
        out List<string> nameDetails,
        out string? machineName
    )
    {
        names = [];
        nameDetails = [];
        machineName = null;

        int pos = 12;
        for (int q = 0; q < qdCount; q++)
        {
            if (!TrySkipDnsName(data, ref pos))
            {
                return false;
            }

            pos += 4; // QTYPE + QCLASS
            if (pos > data.Length)
            {
                return false;
            }
        }

        // First answer resource record: NAME, TYPE(2), CLASS(2), TTL(4), RDLENGTH(2).
        if (!TrySkipDnsName(data, ref pos))
        {
            return false;
        }

        if (pos + 10 > data.Length)
        {
            return false;
        }

        pos += 8; // TYPE + CLASS + TTL
        int rdLength = (data[pos] << 8) | data[pos + 1];
        pos += 2;

        int rdataEnd = pos + rdLength;
        if (rdataEnd > data.Length || pos >= data.Length)
        {
            return false;
        }

        int numNames = data[pos];
        pos += 1;
        if (numNames == 0)
        {
            return false;
        }

        for (int i = 0; i < numNames; i++)
        {
            // Each NODE_NAME entry: 15 bytes name + 1 byte suffix/type + 2 bytes NAME_FLAGS.
            if (pos + 18 > rdataEnd)
            {
                break;
            }

            string rawName = Encoding.ASCII.GetString(data, pos, 15).TrimEnd();
            byte suffix = data[pos + 15];
            int nameFlags = (data[pos + 16] << 8) | data[pos + 17];
            pos += 18;

            // RFC 1002 §4.2.18 NAME_FLAGS (16 bits, MSB first): bit0 G | bits1-2 ONT |
            // bit3 DRG | bit4 CNF | bit5 ACT | bit6 PRM | bits7-15 RESERVED.
            bool isGroup = (nameFlags & 0x8000) != 0;
            OwnerNodeType ownerNodeType = (OwnerNodeType)((nameFlags >> 13) & 0x3);
            bool isBeingDeregistered = (nameFlags & 0x1000) != 0;
            bool isInConflict = (nameFlags & 0x0800) != 0;
            bool isActive = (nameFlags & 0x0400) != 0;
            bool isPermanent = (nameFlags & 0x0200) != 0;

            string key = $"{rawName}<{suffix:X2}>";
            names.Add(key);
            nameDetails.Add(
                string.Join(
                    '|',
                    key,
                    suffix.ToString(CultureInfo.InvariantCulture),
                    GetNetBiosSuffixDescription(suffix),
                    ownerNodeType.ToString(),
                    isGroup.ToString(),
                    isPermanent.ToString(),
                    isActive.ToString(),
                    isInConflict.ToString(),
                    isBeingDeregistered.ToString()
                )
            );

            if (suffix == 0x00 && machineName is null)
            {
                machineName = rawName;
            }
        }

        return names.Count > 0;
    }

    // Advances <paramref name="pos" /> past one DNS-wire-format NAME field: either a
    // 2-byte compression pointer (top two bits of the length byte set), or a sequence
    // of length-prefixed labels terminated by a zero-length byte. NBNS encodes its
    // (single-label) name the same way ordinary DNS names are encoded, so the same
    // walk works for both the query's name and a possibly-compressed answer name.
    private static bool TrySkipDnsName(byte[] data, ref int pos)
    {
        while (true)
        {
            if (pos >= data.Length)
            {
                return false;
            }

            byte len = data[pos];
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 2 > data.Length)
                {
                    return false;
                }

                pos += 2;
                return true;
            }

            if (len == 0)
            {
                pos += 1;
                return true;
            }

            pos += 1 + len;
        }
    }

    // NetBIOS 16th-character suffix meanings — well-known, stable convention (not an IANA
    // registry). Sources: RFC 1001/1002, https://0xffsec.com/handbook/services/netbios/,
    // https://www.ubiqx.org/cifs/Appendix-C.html, https://en.wikipedia.org/wiki/NetBIOS.
    private static string GetNetBiosSuffixDescription(byte suffix) =>
        suffix switch
        {
            0x00 => "Default Name",
            0x01 => "Local Master Browser",
            0x02 => "Local Master Browser",
            0x03 => "Messenger service",
            0x06 => "RAS Server",
            0x1B => "Domain Master Browser",
            0x1C => "Domain Controllers",
            0x1D => "Master Browser",
            0x1E => "Browser Service Elections",
            0x1F => "NetDDE Service",
            0x20 => "File Service (Host Record)",
            0x21 => "RAS Client",
            0x22 => "Microsoft Exchange Interchange",
            0x23 => "Microsoft Exchange Store",
            0x24 => "Microsoft Exchange Directory",
            0x2B => "IBM Lotus Notes Server",
            0x2F => "IBM Lotus Notes",
            0x30 => "Modem Sharing Server",
            0x31 => "Modem Sharing Client",
            0x33 => "IBM Lotus Notes",
            0x42 => "McAfee Antivirus",
            0x43 => "SMS Clients Remote Control",
            0x44 => "SMS Administrators Remote Control Tool",
            0x45 => "SMS Clients Remote Chat",
            0x46 => "SMS Clients Remote Transfer",
            0x4C => "DEC Pathworks TCPIP",
            0x52 => "DEC Pathworks TCPIP",
            0x6A => "Microsoft Exchange IMC",
            0x87 => "Microsoft Exchange MTA",
            0xBE => "Network Monitor Agent",
            0xBF => "Network Monitor Application",
            _ => $"Unknown Suffix (0x{suffix:X2})",
        };
}