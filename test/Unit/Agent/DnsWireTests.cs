using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Unit tests for <see cref="DnsWire" /> — the DNS wire-format name decoder shared by
/// <see cref="MdnsScanner" /> and <see cref="LlmnrScanner" /> (review D22). This parses
/// untrusted network input, so coverage focuses on the malformed-input paths (truncated
/// packets, a compression-pointer loop) as much as the happy path.
/// </summary>
public sealed class DnsWireTests
{
    [Fact]
    public void ReadName_UncompressedName_DecodesLabelsJoinedByDots()
    {
        byte[] packet = [3, (byte)'f', (byte)'o', (byte)'o', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0];
        int offset = 0;

        string name = DnsWire.ReadName(packet, ref offset);

        Assert.Equal("foo.local", name);
        Assert.Equal(packet.Length, offset);
    }

    [Fact]
    public void ReadName_RootLabel_ReturnsEmptyString()
    {
        byte[] packet = [0];
        int offset = 0;

        string name = DnsWire.ReadName(packet, ref offset);

        Assert.Equal("", name);
        Assert.Equal(1, offset);
    }

    [Fact]
    public void ReadName_CompressionPointer_FollowsPointerAndRestoresOffsetAfterPointer()
    {
        // "local" stored once at offset 0; a second name at offset 7 is "foo" + a pointer back to 0.
        byte[] packet =
        [
            5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0, // offset 0..6: "local"
            3, (byte)'f', (byte)'o', (byte)'o', 0xC0, 0, // offset 7..12: "foo" + pointer to 0
        ];
        int offset = 7;

        string name = DnsWire.ReadName(packet, ref offset);

        Assert.Equal("foo.local", name);
        // Offset lands right after the 2-byte pointer, not wherever the pointer jumped to.
        Assert.Equal(13, offset);
    }

    [Fact]
    public void ReadName_PointerLoop_StopsInsteadOfHanging()
    {
        // Byte 0 is a pointer to itself — a malicious/corrupt packet trying to spin the parser forever.
        byte[] packet = [0xC0, 0];
        int offset = 0;

        string name = DnsWire.ReadName(packet, ref offset);

        // Must return (not hang) — the hop guard caps how many times it follows the pointer.
        Assert.Equal("", name);
    }

    [Fact]
    public void ReadName_LengthByteExceedsPacketBounds_StopsWithoutThrowing()
    {
        // Claims a 10-byte label but only 2 bytes follow.
        byte[] packet = [10, (byte)'a', (byte)'b'];
        int offset = 0;

        string name = DnsWire.ReadName(packet, ref offset);

        Assert.Equal("", name);
    }

    [Fact]
    public void ReadName_TruncatedPointer_StopsWithoutThrowing()
    {
        // A compression-pointer marker with no second byte to complete it.
        byte[] packet = [0xC0];
        int offset = 0;

        string name = DnsWire.ReadName(packet, ref offset);

        Assert.Equal("", name);
    }

    [Fact]
    public void SkipName_UncompressedName_AdvancesPastTerminatingZero()
    {
        byte[] packet = [3, (byte)'f', (byte)'o', (byte)'o', 0, 99];
        int offset = 0;

        DnsWire.SkipName(packet, ref offset);

        Assert.Equal(5, offset);
    }

    [Fact]
    public void SkipName_CompressionPointer_AdvancesExactlyTwoBytesWithoutFollowingIt()
    {
        byte[] packet = [0xC0, 0, 99];
        int offset = 0;

        DnsWire.SkipName(packet, ref offset);

        Assert.Equal(2, offset);
    }

    [Fact]
    public void SkipName_TruncatedLabel_AdvancesPastPacketEndWithoutThrowing()
    {
        // SkipName (unlike ReadName) doesn't bounds-check the label length against the packet —
        // it just computes where the name *would* end and lets the loop condition stop it; a
        // caller that then indexes at the returned offset without its own bounds check would be
        // the actual bug, not this method. Documenting the real behavior here so a future change
        // that "fixes" this doesn't silently alter it without a test noticing.
        byte[] packet = [10, (byte)'a', (byte)'b'];
        int offset = 0;

        DnsWire.SkipName(packet, ref offset);

        Assert.Equal(11, offset); // 1 (length byte) + 10 (claimed label length), never clamped
        Assert.True(offset > packet.Length);
    }
}