using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace JMW.Discovery.Core;

public enum FactValueKind : byte
{
    Null,
    String,
    Long,
    Double,
    Bool,
    DateTimeOffset, // ticks (long) in _long; always UTC
    TimeSpan, // ticks (long) in _long
    IPv4Address, // uint in _long (network byte order)
    IPv6Address, // lower 64 bits in _long, upper 64 bits in _long2
    IPPrefix, // IP bits same as IPv4/IPv6; prefix length (byte) in bits 32-39 of _long2
    MacAddress, // 48-bit MAC in _long (big-endian, upper 16 bits zero)
}

/// <summary>
/// Discriminated union holding a fact's value without boxing or heap allocation.
/// Storage layout (LayoutKind.Explicit, 32 bytes):
/// offset  0  FactValueKind _kind
/// offset  8  string?       _str      (reference, isolated offset)
/// offset 16  long          _long     (also aliased as double for Double kind)
/// offset 24  long          _long2    (upper 64 bits for IPv6 and IPPrefix)
/// Runtime types (IPAddress, IPNetwork, PhysicalAddress) are all reference types or
/// wrap reference types internally, making them unsuitable for direct storage here.
/// Use the From*/To* helpers to convert at API boundaries.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
[JsonConverter(typeof(FactValueJsonConverter))]
public readonly struct FactValue : IEquatable<FactValue>
{
    [FieldOffset(0)]
    private readonly FactValueKind _kind;

    [FieldOffset(8)]
    private readonly string? _str;

    [FieldOffset(16)]
    private readonly long _long;

    [FieldOffset(16)]
    private readonly double _double; // overlaps _long

    [FieldOffset(24)]
    private readonly long _long2; // IPv6 upper / prefix len

    private FactValue(FactValueKind kind, string? str, long lo, long hi = 0) : this()
    {
        _kind = kind;
        _str = str;
        _long = lo;
        _long2 = hi;
    }

    private FactValue(double value) : this()
    {
        _kind = FactValueKind.Double;
        _double = value;
    }

    // Explicitly the Null-kind value. Identical to default(FactValue) (Null is the
    // zero enum member) but assigned so the intent is stated, not inferred.
    public static readonly FactValue Null = new(FactValueKind.Null, null, 0);

    // ── Primitive factories ───────────────────────────────────────────────────

    // NUL-stripped here (not just at the DB write boundary) so every consumer of a
    // FactValue — not only FactRepository's ingest path — sees already-clean text.
    public static FactValue FromString(string value) => new(FactValueKind.String, TextSanitizer.StripNul(value), 0);
    public static FactValue FromLong(long value) => new(FactValueKind.Long, null, value);
    public static FactValue FromDouble(double value) => new(value);
    public static FactValue FromBool(bool value) => new(FactValueKind.Bool, null, value ? 1L : 0L);
    public static FactValue FromDateTimeOffset(DateTimeOffset v) => new(FactValueKind.DateTimeOffset, null, v.UtcTicks);
    public static FactValue FromTimeSpan(TimeSpan value) => new(FactValueKind.TimeSpan, null, value.Ticks);

    // ── Network factories — accept runtime types; extract bits zero-alloc ─────

    public static FactValue FromIPv4(uint networkOrderBytes) =>
        new(FactValueKind.IPv4Address, null, networkOrderBytes);

    public static FactValue FromIPAddress(IPAddress address)
    {
        Span<byte> buf = stackalloc byte[16];
        address.TryWriteBytes(buf, out _);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            uint v4 = (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
            return new(FactValueKind.IPv4Address, null, v4);
        }

        long lo = BinaryPrimitives.ReadInt64BigEndian(buf);
        long hi = BinaryPrimitives.ReadInt64BigEndian(buf[8..]);
        return new(FactValueKind.IPv6Address, null, lo, hi);
    }

    public static FactValue FromIPNetwork(IPNetwork network)
    {
        FactValue baseVal = FromIPAddress(network.BaseAddress);
        byte prefixLen = (byte)network.PrefixLength;
        // _long2 layout: bits 0-31 = upper 32 bits of IPv6 address (0 for IPv4)
        //                bits 32-39 = prefix length
        //                bit  40    = 1 if IPv4, 0 if IPv6 (needed to reconstruct ToIPAddress correctly)
        long hiWithPrefix = baseVal._kind == FactValueKind.IPv4Address
            ? (1L << 40) | ((long)prefixLen << 32)
            : baseVal._long2 | ((long)prefixLen << 32);
        return new(FactValueKind.IPPrefix, null, baseVal._long, hiWithPrefix);
    }

    public static FactValue FromMacAddress(long macBigEndian48) =>
        new(FactValueKind.MacAddress, null, macBigEndian48 & 0x0000_FFFF_FFFF_FFFFL);

    public static FactValue FromPhysicalAddress(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes(); // allocates once; unavoidable with PhysicalAddress
        long mac = 0;
        foreach (byte b in bytes)
        {
            mac = (mac << 8) | b;
        }

        return new(FactValueKind.MacAddress, null, mac);
    }

    // ── Accessors ─────────────────────────────────────────────────────────────

    public FactValueKind Kind => _kind;

    public string? AsString() => _kind == FactValueKind.String ? _str : null;
    public long? AsLong() => _kind == FactValueKind.Long ? _long : null;
    public double? AsDouble() => _kind == FactValueKind.Double ? _double : null;
    public bool? AsBool() => _kind == FactValueKind.Bool ? _long != 0 : null;

    public DateTimeOffset? AsDateTimeOffset()
        => _kind == FactValueKind.DateTimeOffset ? new DateTimeOffset(_long, TimeSpan.Zero) : null;

    public TimeSpan? AsTimeSpan() => _kind == FactValueKind.TimeSpan ? TimeSpan.FromTicks(_long) : null;

    // bit 40 of _long2 is set when IPPrefix wraps an IPv4 address
    private bool IsIPv4Prefix => _kind == FactValueKind.IPPrefix && (_long2 & (1L << 40)) != 0;

    /// <summary>Constructs a runtime IPAddress from stored bits. Allocates (by design of IPAddress).</summary>
    public IPAddress? ToIPAddress()
    {
        if (_kind == FactValueKind.IPv4Address || IsIPv4Prefix)
        {
            Span<byte> b = stackalloc byte[4];
            b[0] = (byte)(_long >> 24);
            b[1] = (byte)(_long >> 16);
            b[2] = (byte)(_long >> 8);
            b[3] = (byte)_long;
            return new IPAddress(b);
        }

        if (_kind is FactValueKind.IPv6Address or FactValueKind.IPPrefix)
        {
            Span<byte> b = stackalloc byte[16];
            BinaryPrimitives.WriteInt64BigEndian(b, _long);
            BinaryPrimitives.WriteInt64BigEndian(b[8..], _long2 & 0x0000_0000_FFFF_FFFF);
            return new IPAddress(b);
        }

        return null;
    }

    /// <summary>Constructs a runtime IPNetwork. Allocates (IPNetwork wraps IPAddress).</summary>
    public IPNetwork? ToIPNetwork()
    {
        if (_kind != FactValueKind.IPPrefix)
        {
            return null;
        }

        int prefixLen = (int)((_long2 >> 32) & 0xFF);
        return new IPNetwork(
            ToIPAddress() ?? throw new InvalidOperationException("IPPrefix value has no IP address."),
            prefixLen
        );
    }

    public override string? ToString() => _kind switch
    {
        FactValueKind.String => _str,
        FactValueKind.Long => _long.ToString(),
        FactValueKind.Double => _double.ToString(CultureInfo.InvariantCulture),
        FactValueKind.Bool => _long != 0 ? "true" : "false",
        FactValueKind.DateTimeOffset => new DateTimeOffset(_long, TimeSpan.Zero).ToString("O"),
        FactValueKind.TimeSpan => TimeSpan.FromTicks(_long).ToString(),
        FactValueKind.IPv4Address =>
            $"{(_long >> 24) & 0xFF}.{(_long >> 16) & 0xFF}.{(_long >> 8) & 0xFF}.{_long & 0xFF}",
        FactValueKind.IPv6Address => ToIPAddress()?.ToString(),
        FactValueKind.IPPrefix => $"{ToIPAddress()}/{(_long2 >> 32) & 0xFF}",
        FactValueKind.MacAddress =>
            $"{(_long >> 40) & 0xFF:X2}:{(_long >> 32) & 0xFF:X2}:{(_long >> 24) & 0xFF:X2}:{(_long >> 16) & 0xFF:X2}:{(_long >> 8) & 0xFF:X2}:{_long & 0xFF:X2}",
        _ => null,
    };

    // Equality: compare raw bits — _long2 is zero for non-IP kinds, safe for all.
    public bool Equals(FactValue other)
        => _kind == other._kind && _str == other._str && _long == other._long && _long2 == other._long2;

    public override bool Equals(object? obj) => obj is FactValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(_kind, _str, _long, _long2);

    public static bool operator ==(FactValue l, FactValue r) => l.Equals(r);
    public static bool operator !=(FactValue l, FactValue r) => !l.Equals(r);
}