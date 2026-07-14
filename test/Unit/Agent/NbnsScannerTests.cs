using System.Text;

using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests <see cref="NbnsScanner.ParseResponse" /> against synthetic NBSTAT (RFC 1002
/// §4.2.1.1 / §4.2.18) wire responses, built independently of the scanner's own encoder.
/// The previous implementation assumed a fixed 62-byte offset to the name table, which
/// only held when the response echoed the question section AND the answer name used
/// DNS compression. Real responders that answer without echoing the question (a legal,
/// observed shape) silently misaligned parsing and produced garbled, overlapping names.
/// This also covers the header flags and per-name flags/suffix data now extracted, since
/// a reference implementation consulted while fixing the offset bug had its own bit-
/// extraction bug (an off-by-one loop bound that made RCODE always decode as 0) -- these
/// bit positions are independently verified against the RFC text, not ported from it.
/// </summary>
public sealed class NbnsScannerTests
{
    private static byte[] EncodeName(string name, byte suffix)
    {
        byte[] padded = new byte[16];
        byte[] nameBytes = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
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

    private static byte[] NodeNameEntry(string name, byte suffix, ushort nameFlags)
    {
        byte[] entry = new byte[18];
        byte[] nameBytes = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
        Array.Copy(nameBytes, entry, Math.Min(nameBytes.Length, 15));
        for (int i = nameBytes.Length; i < 15; i++)
        {
            entry[i] = 0x20;
        }

        entry[15] = suffix;
        entry[16] = (byte)(nameFlags >> 8);
        entry[17] = (byte)(nameFlags & 0xFF);
        return entry;
    }

    /// <summary>Header with a fixed, "normal successful response" flags word (0x8400):
    /// R=1, OPCODE=Query(0), AA=1, everything else 0, RCODE=Success(0).</summary>
    private static byte[] Header(ushort qd, ushort an) =>
    [
        0xAB, 0xCD, // transaction id
        0x84, 0x00, // flags word -- see summary above
        (byte)(qd >> 8), (byte)(qd & 0xFF),
        (byte)(an >> 8), (byte)(an & 0xFF),
        0x00, 0x00, // NSCOUNT
        0x00, 0x00, // ARCOUNT
    ];

    private static byte[] Rdata(params (string Name, byte Suffix, ushort NameFlags)[] names)
    {
        List<byte> b = [(byte)names.Length];
        foreach ((string n, byte s, ushort f) in names)
        {
            b.AddRange(NodeNameEntry(n, s, f));
        }

        return [.. b];
    }

    private static byte[] BuildPacket(byte[] header, byte[]? questionName, byte[] answerName, byte[] rdata)
    {
        List<byte> packet = [.. header];
        if (questionName is not null)
        {
            packet.AddRange(questionName);
            packet.AddRange([0x00, 0x21, 0x00, 0x01]); // QTYPE NBSTAT, QCLASS IN
        }

        packet.AddRange(answerName);
        packet.AddRange([0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]); // type, class, ttl
        packet.Add((byte)(rdata.Length >> 8));
        packet.Add((byte)(rdata.Length & 0xFF));
        packet.AddRange(rdata);
        return [.. packet];
    }

    [Fact]
    public void ParseResponse_QuestionEchoedWithCompressedAnswerName_ParsesNameTable()
    {
        byte[] rdata = Rdata(("MYPC", 0x00, 0x0400), ("WORKGROUP", 0x00, 0x8400));
        byte[] questionName = [0x20, .. EncodeName("*", 0x00), 0x00];
        byte[] packet = BuildPacket(Header(1, 1), questionName, [0xC0, 0x0C], rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.10", packet);

        Assert.NotNull(device);
        Assert.Equal("MYPC", device.Hostname);
        Assert.Equal("MYPC<00>,WORKGROUP<00>", device.Attributes["nbns.names"]);
    }

    [Fact]
    public void ParseResponse_QuestionOmittedWithUncompressedAnswerName_ParsesNameTable()
    {
        // Some responders skip echoing the question section (QDCOUNT=0) and spell the
        // answer's own name out in full rather than compressing it -- exactly the shape
        // the old fixed-offset parser misread as garbled, overlapping name fragments.
        byte[] rdata = Rdata(("-NAS-40", 0x00, 0x0400));
        byte[] answerName = [0x20, .. EncodeName("-NAS-40", 0x00), 0x00];
        byte[] packet = BuildPacket(Header(0, 1), null, answerName, rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.71", packet);

        Assert.NotNull(device);
        Assert.Equal("-NAS-40", device.Hostname);
        Assert.Equal("-NAS-40<00>", device.Attributes["nbns.names"]);
    }

    [Fact]
    public void ParseResponse_MultipleNamesAcrossBothShapes_AllParseCleanly()
    {
        byte[] rdata = Rdata(("-NAS-40", 0x00, 0x0400), ("WORKGROUP", 0x00, 0x8400), ("WORKGROUP", 0x1E, 0x8400));
        byte[] answerName = [0x20, .. EncodeName("-NAS-40", 0x00), 0x00];
        byte[] packet = BuildPacket(Header(0, 1), null, answerName, rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.71", packet);

        Assert.NotNull(device);
        Assert.Equal("-NAS-40<00>,WORKGROUP<00>,WORKGROUP<1E>", device.Attributes["nbns.names"]);
    }

    [Fact]
    public void ParseResponse_HeaderFlags_DecodeFromTheNormalSuccessCase()
    {
        // Header(...) uses flags word 0x8400: R=1, OPCODE=Query, AA=1, TC/RD/RA/B=0, RCODE=Success.
        byte[] rdata = Rdata(("MYPC", 0x00, 0x0400));
        byte[] answerName = [0x20, .. EncodeName("MYPC", 0x00), 0x00];
        byte[] packet = BuildPacket(Header(0, 1), null, answerName, rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.10", packet);

        Assert.NotNull(device);
        Assert.Equal("Query", device.Attributes["nbns.op_code"]);
        Assert.Equal("Success", device.Attributes["nbns.result_code"]);
        Assert.Equal("True", device.Attributes["nbns.authoritative"]);
        Assert.Equal("False", device.Attributes["nbns.truncated"]);
        Assert.Equal("False", device.Attributes["nbns.broadcast"]);
        Assert.Equal("False", device.Attributes["nbns.recursion_desired"]);
        Assert.Equal("False", device.Attributes["nbns.recursion_available"]);
    }

    [Fact]
    public void ParseResponse_HeaderFlags_DecodeEveryDistinctBitIndependently()
    {
        // A deliberately varied flags word, hand-derived from the RFC 1002 §4.2.1.1 bit
        // diagram (not from the reference implementation): R=1, OPCODE=Registration(5),
        // AA=0, TC=1, RD=1, RA=1, B=1, RCODE=NamErr(3) -> 0xAB93. Verifies every field
        // independently rather than relying on a single all-zero/all-one pattern that
        // could mask a bit swapped for its neighbor.
        byte[] header =
        [
            0xAB, 0xCD,
            0xAB, 0x93,
            0x00, 0x00, // QDCOUNT
            0x00, 0x01, // ANCOUNT
            0x00, 0x00,
            0x00, 0x00,
        ];
        byte[] rdata = Rdata(("MYPC", 0x00, 0x0400));
        byte[] answerName = [0x20, .. EncodeName("MYPC", 0x00), 0x00];
        byte[] packet = BuildPacket(header, null, answerName, rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.10", packet);

        Assert.NotNull(device);
        Assert.Equal("Registration", device.Attributes["nbns.op_code"]);
        Assert.Equal("NamErr", device.Attributes["nbns.result_code"]);
        Assert.Equal("False", device.Attributes["nbns.authoritative"]);
        Assert.Equal("True", device.Attributes["nbns.truncated"]);
        Assert.Equal("True", device.Attributes["nbns.broadcast"]);
        Assert.Equal("True", device.Attributes["nbns.recursion_desired"]);
        Assert.Equal("True", device.Attributes["nbns.recursion_available"]);
    }

    [Fact]
    public void ParseResponse_NameFlags_DecodeSuffixAndAllFiveFlagsIndependently()
    {
        // NAME_FLAGS 0xD400, hand-derived from the RFC 1002 §4.2.18 bit diagram: G=1,
        // ONT=Mixed(0b10), DRG=1, CNF=0, ACT=1, PRM=0.
        byte[] rdata = Rdata(("FILESRV", 0x20, 0xD400));
        byte[] answerName = [0x20, .. EncodeName("FILESRV", 0x20), 0x00];
        byte[] packet = BuildPacket(Header(0, 1), null, answerName, rdata);

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.10", packet);

        Assert.NotNull(device);
        string detail = Assert.Single(
            device.Attributes["nbns.name_details"].Split(',', StringSplitOptions.RemoveEmptyEntries)
        );
        string[] fields = detail.Split('|');
        Assert.Equal("FILESRV<20>", fields[0]);
        Assert.Equal("32", fields[1]); // suffix, decimal
        Assert.Equal("File Service (Host Record)", fields[2]);
        Assert.Equal("Mixed", fields[3]);
        Assert.Equal("True", fields[4]); // IsGroup
        Assert.Equal("False", fields[5]); // IsPermanent
        Assert.Equal("True", fields[6]); // IsActive
        Assert.Equal("False", fields[7]); // IsInConflict
        Assert.Equal("True", fields[8]); // IsBeingDeregistered
    }

    [Fact]
    public void ParseResponse_TruncatedPacket_ReturnsNull() =>
        Assert.Null(NbnsScanner.ParseResponse("192.168.1.10", new byte[8]));

    [Fact]
    public void ParseResponse_ZeroAnswerCount_StillReturnsHeaderOnlyDevice()
    {
        // A structurally valid response with no answer record still confirms this host
        // speaks NBNS -- that's real signal, not nothing, so it's kept (header attributes
        // only; no name-table attributes since there's no answer to read one from).
        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.10", Header(0, 0));

        Assert.NotNull(device);
        Assert.Null(device.Hostname);
        Assert.Equal("Success", device.Attributes["nbns.result_code"]);
        Assert.False(device.Attributes.ContainsKey("nbns.names"));
        Assert.False(device.Attributes.ContainsKey("nbns.name_details"));
    }

    [Fact]
    public void ParseResponse_RdataShorterThanDeclaredNames_StopsAtRdataBoundary()
    {
        // rdlength deliberately understates the real byte count so the parser must stop
        // at the RDATA boundary rather than reading into whatever memory follows.
        byte[] fullRdata = Rdata(("-NAS-40", 0x00, 0x0400), ("WORKGROUP", 0x00, 0x8400));
        int truncatedRdLength = 1 + 18; // NUM_NAMES byte + exactly one 18-byte entry
        byte[] answerName = [0x20, .. EncodeName("-NAS-40", 0x00), 0x00];

        List<byte> packet =
        [
            .. Header(0, 1),
            .. answerName,
            0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            (byte)(truncatedRdLength >> 8), (byte)(truncatedRdLength & 0xFF),
            .. fullRdata,
        ];

        DiscoveredDevice? device = NbnsScanner.ParseResponse("192.168.1.71", [.. packet]);

        Assert.NotNull(device);
        Assert.Equal("-NAS-40<00>", device.Attributes["nbns.names"]);
    }
}