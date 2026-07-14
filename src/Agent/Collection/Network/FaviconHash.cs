using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Hashes for a device's favicon — a firmware-baked static asset that is stable
/// per product/firmware, so it makes a compact device fingerprint.
///
/// Two hashes are produced because two ecosystems key on different algorithms and
/// they are NOT interchangeable:
/// <list type="bullet">
///   <item>Rapid7 Recog (<c>favicons.xml</c>) keys on <b>MD5 of the raw bytes</b> — <see cref="Md5Hex"/>.</item>
///   <item>Shodan/Censys key on <b>MurmurHash3 x86 32-bit (seed 0, signed)</b> over the favicon
///   <b>base64-encoded with a newline every 76 chars</b> (MIME style, i.e. Python
///   <c>base64.encodebytes</c>) — <see cref="ShodanHash"/>. The newlines are part of the hashed
///   input; omitting them silently mismatches Shodan's index. This is the #1 implementation gotcha.</item>
/// </list>
/// </summary>
public static class FaviconHash
{
    /// <summary>Lowercase-hex MD5 of the raw favicon bytes (for Recog <c>favicons.xml</c>).</summary>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "MD5 is the key algorithm Recog favicons.xml uses; this is fingerprint matching, not security."
    )]
    public static string Md5Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Shodan-compatible favicon hash: MurmurHash3 x86_32 (seed 0, signed) over the favicon
    /// base64-encoded MIME-style (newline every 76 chars, trailing newline), matching Python
    /// <c>mmh3.hash(base64.encodebytes(bytes))</c>.
    /// </summary>
    public static int ShodanHash(ReadOnlySpan<byte> faviconBytes)
    {
        string mime = ToMimeBase64(faviconBytes);
        return MurmurHash3(Encoding.ASCII.GetBytes(mime));
    }

    /// <summary>
    /// Base64 with a '\n' after every 76 output characters and a trailing '\n' — byte-for-byte
    /// equivalent to Python <c>base64.encodebytes</c>. NOT .NET's
    /// <c>Base64FormattingOptions.InsertLineBreaks</c>, which emits CRLF and would change the hash.
    /// </summary>
    public static string ToMimeBase64(ReadOnlySpan<byte> data)
    {
        string flat = Convert.ToBase64String(data);
        StringBuilder sb = new(flat.Length + (flat.Length / 76) + 2);
        for (int i = 0; i < flat.Length; i += 76)
        {
            sb.Append(flat.AsSpan(i, Math.Min(76, flat.Length - i)));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// MurmurHash3 x86 32-bit variant (Austin Appleby's reference algorithm), returned signed as
    /// Shodan indexes it.
    /// </summary>
    public static int MurmurHash3(ReadOnlySpan<byte> data, uint seed = 0)
    {
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;

        uint h1 = seed;
        int length = data.Length;
        int roundedEnd = length & ~0x3;

        for (int i = 0; i < roundedEnd; i += 4)
        {
            uint k1 = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            k1 *= c1;
            k1 = BitOperations.RotateLeft(k1, 15);
            k1 *= c2;

            h1 ^= k1;
            h1 = BitOperations.RotateLeft(h1, 13);
            h1 = (h1 * 5) + 0xe6546b64;
        }

        uint tailK = 0;
        int tail = length & 0x3;
        if (tail == 3)
        {
            tailK ^= (uint)data[roundedEnd + 2] << 16;
        }

        if (tail >= 2)
        {
            tailK ^= (uint)data[roundedEnd + 1] << 8;
        }

        if (tail >= 1)
        {
            tailK ^= data[roundedEnd];
            tailK *= c1;
            tailK = BitOperations.RotateLeft(tailK, 15);
            tailK *= c2;
            h1 ^= tailK;
        }

        h1 ^= (uint)length;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6b;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35;
        h1 ^= h1 >> 16;

        return unchecked((int)h1);
    }
}